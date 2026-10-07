using System.IO;
using System.Text.Json;
using TestStation.Application;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Domain;

namespace TestStation.Smoke;

internal static class DocumentRegression
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static string Snapshot(SequenceDefinition sequence) => JsonSerializer.Serialize(sequence);
    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);
    private static SequenceStep Step(string id, string kind = "comment") => new() { Id = id, Kind = kind, Name = id };
    private static SequenceDefinition Nested() => new() {
        Id = "a", Name = "A 序列", SetupSteps = [Step("setup")], CleanupSteps = [Step("cleanup")],
        Steps = [new SequenceStep { Id = "if", Kind = "if", Name = "IF", Params = new() { ["expression"] = Value("true") },
            Children = [new SequenceStep { Id = "for", Kind = "loop", Name = "FOR", Params = new() { ["variable"] = Value("i") },
                Children = [Step("child")] }], ElseChildren = [Step("else")] }],
        Variables = [new() { Name = "limit", DataType = "number", Default = Value(12.5) }]
    };
    private static (MainViewModel Model, TestSequenceDialogs Dialogs, ProjectWorkspace Workspace) Fixture()
    {
        var workspace = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-documents", Guid.NewGuid().ToString("N")), "project");
        workspace.SaveSequence(Nested());
        workspace.SaveSequence(new SequenceDefinition { Id = "b", Name = "B 序列", Steps = [Step("second")] });
        var dialogs = new TestSequenceDialogs();
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: dialogs);
        model.OpenProject(workspace.Root);
        return (model, dialogs, workspace);
    }
    private static SequenceStep Find(MainViewModel model, string id) => SequenceEditor.Walk(model.SelectedSequence!).Single(step => step.Id == id);
    private static SequenceRow Row(MainViewModel model, string id) => model.Rows.Single(row => row.RowType == "step" && row.Step?.Id == id);

    private static async Task VerifyAsync()
    {
        VerifyHistory();
        await VerifyEditsAndManagementAsync();
        await VerifyUnsavedChoicesAsync();
        await VerifyRunLocksAsync();
        Console.WriteLine("PASS: document history, nested undo/redo, save/discard/cancel, management, save failures, execution/Cleanup locks and safe project switching");
    }

    private static void VerifyHistory()
    {
        var service = new SequenceDocumentService();
        var sequence = Nested(); var session = service.Track(sequence); var original = Snapshot(sequence);
        Require(!session.IsDirty && !session.CanUndo, "Loaded sequence starts dirty");
        session.Edit(model => SequenceEditor.Move(model, "for", new("Cleanup", null, false, 1)));
        var moved = Snapshot(sequence);
        Require(session.IsDirty && sequence.CleanupSteps.Last().Children.Single().Id == "child", "Move history lost nested children");
        Require(session.Undo() && Snapshot(sequence) == original && !session.IsDirty, "Undo did not restore saved nested model");
        Require(session.Redo() && Snapshot(sequence) == moved, "Redo changed moved structure");
        session.MarkSaved(); session.Edit(model => SequenceEditor.Remove(model, "if"));
        Require(session.Undo() && Snapshot(sequence) == moved && !session.IsDirty, "Container delete undo did not reach saved baseline");
        Require(!session.Edit(_ => { }) && session.CanRedo, "No-op destroyed redo");
        try { session.Edit(model => { model.Name = "broken"; model.Steps.Clear(); throw new ArgumentException("expected"); }); }
        catch (ArgumentException) { }
        Require(Snapshot(sequence) == moved && session.CanRedo, "Failed transaction was not rolled back");
        session.Edit(model => model.Description = "new edit");
        Require(!session.CanRedo && session.Discard() && Snapshot(sequence) == moved && !session.CanUndo, "New edit/discard history is incorrect");
        var copy = service.Duplicate(sequence, [sequence.Id]);
        Require(copy.Id != sequence.Id && !SequenceEditor.Walk(copy).Select(step => step.Id).Intersect(SequenceEditor.Walk(sequence).Select(step => step.Id)).Any(),
            "Copy retained sequence or nested step IDs");
        Require(copy.CleanupSteps.Last().Children.Count == 1 && copy.Variables.Single().Default?.GetDouble() == 12.5, "Copy lost structure/variables");
        copy.CleanupSteps.Last().Children.Clear();
        Require(sequence.CleanupSteps.Last().Children.Count == 1, "Copy shares mutable subtrees");
    }

    private static async Task VerifyEditsAndManagementAsync()
    {
        var (model, dialogs, workspace) = Fixture();
        var original = Snapshot(model.SelectedSequence!);
        model.MoveStep("for", new("Main", "if", true, 0));
        Require(Find(model, "if").ElseChildren.First().Id == "for", "Move into ELSE failed");
        model.UndoSequenceCommand.Execute(null);
        Require(Snapshot(model.SelectedSequence!) == original && !model.IsSequenceDirty, "ViewModel nested undo failed");
        model.RedoSequenceCommand.Execute(null);
        Require(Find(model, "if").ElseChildren.First().Id == "for", "ViewModel nested redo failed");
        model.SelectedRow = Row(model, "child"); model.StepName = "重命名子步骤"; model.StepDescription = "检查历史";
        model.ApplyStepCommand.Execute(null);
        model.UndoSequenceCommand.Execute(null);
        Require(Find(model, "child").Name == "child", "Property undo failed");
        model.RedoSequenceCommand.Execute(null);
        Require(Find(model, "child").Description == "检查历史", "Property redo failed");
        Row(model, "child").Enabled = false;
        model.UndoSequenceCommand.Execute(null); Require(Find(model, "child").Enabled, "Enabled undo failed");
        model.RedoSequenceCommand.Execute(null); Require(!Find(model, "child").Enabled, "Enabled redo failed");
        model.SelectedVariable = model.Variables.Single(); model.VariableDefault = "20"; model.ApplyVariableCommand.Execute(null);
        model.UndoSequenceCommand.Execute(null); Require(model.Variables.Single().Default?.GetDouble() == 12.5, "Variable undo failed");
        model.RedoSequenceCommand.Execute(null); Require(model.Variables.Single().Default?.GetDouble() == 20, "Variable redo failed");
        model.SelectedVariable = model.Variables.Single(); model.DeleteVariableCommand.Execute(null);
        model.UndoSequenceCommand.Execute(null); Require(model.Variables.Count == 1, "Variable deletion undo failed");
        model.SaveSequenceCommand.Execute(null); Require(!model.IsSequenceDirty, "Save did not clear dirty state");
        dialogs.Name = "更名序列"; await model.RenameSequenceCommand.ExecuteAsync(null);
        Require(model.SelectedSequence!.Name == "更名序列" && model.IsSequenceDirty, "Rename was not tracked");
        model.UndoSequenceCommand.Execute(null); Require(!model.IsSequenceDirty, "Rename undo did not reach saved snapshot");
        model.RedoSequenceCommand.Execute(null); model.SaveSequenceCommand.Execute(null);
        var source = model.SelectedSequence;
        await model.DuplicateSequenceCommand.ExecuteAsync(null);
        var duplicate = model.SelectedSequence!;
        Require(duplicate.Id != source!.Id && duplicate.Steps.Single().ElseChildren.First().Children.Single().Description == "检查历史",
            "Sequence copy did not retain edited nested structure");
        Require(model.IsSequenceDirty && model.Sequences.Count == 3, "Duplicate is not a new unsaved document");
        model.SaveSequenceCommand.Execute(null);
        var disk = workspace.LoadSequences().Single(item => item.Id == duplicate.Id);
        Require(Snapshot(disk) == Snapshot(duplicate), "Copy persistence changed structure");
        dialogs.Delete = false; await model.DeleteSequenceCommand.ExecuteAsync(null);
        Require(model.SelectedSequence == duplicate && File.Exists(Path.Combine(workspace.Root, "sequences", duplicate.Id + ".tsseq")), "Cancelled deletion deleted data");
        dialogs.Delete = true; await model.DeleteSequenceCommand.ExecuteAsync(null);
        Require(model.Sequences.Count == 2 && !File.Exists(Path.Combine(workspace.Root, "sequences", duplicate.Id + ".tsseq")), "Confirmed deletion did not remove the file");
        var target = model.SelectedSequence!;
        var caller = new SequenceDefinition { Id = "caller", Name = "调用者", Steps = [new() { Id = "call", Kind = "call_sequence", Name = "调用", Params = new() { ["sequence_id"] = Value(target.Id) } }] };
        model.Sequences.Add(caller); var confirmations = dialogs.DeleteCalls;
        await model.DeleteSequenceCommand.ExecuteAsync(null);
        Require(model.SelectedSequence == target && dialogs.DeleteCalls == confirmations && model.Status.Contains("调用者"), "In-use sequence was deleted");
        var reopened = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService());
        reopened.OpenProject(workspace.Root);
        Require(reopened.Sequences.Single(item => item.Id == source.Id).Name == "更名序列", "Rename did not survive reopen");
    }

    private static async Task VerifyUnsavedChoicesAsync()
    {
        var (model, dialogs, workspace) = Fixture(); var a = model.SelectedSequence!; var b = model.Sequences.Single(item => item.Id == "b");
        dialogs.Choice = SequenceUnsavedChoice.Cancel; model.SequenceName = "pending";
        Require(!await model.SelectSequenceAsync(b) && model.SelectedSequence == a && model.SequenceName == "pending", "Cancel lost pending metadata");
        dialogs.Choice = SequenceUnsavedChoice.Save;
        Require(await model.SelectSequenceAsync(b) && workspace.LoadSequences().Single(item => item.Id == a.Id).Name == "pending", "Save before switch failed");
        await model.SelectSequenceAsync(a); model.SequenceName = "discard me"; Row(model, "child").Enabled = false;
        dialogs.Choice = SequenceUnsavedChoice.Discard;
        Require(await model.SelectSequenceAsync(b) && a.Name == "pending" && SequenceEditor.Walk(a).Single(item => item.Id == "child").Enabled,
            "Discard did not restore saved metadata and Enabled");
        await model.NewSequenceCommand.ExecuteAsync(null); var unsaved = model.SelectedSequence!;
        Require(model.IsSequenceDirty, "New empty sequence is incorrectly considered saved");
        await model.SelectSequenceAsync(b); Require(!model.Sequences.Contains(unsaved), "Discard left an unsaved new document in the list");
        await model.SelectSequenceAsync(a); model.SelectedRow = Row(model, "child"); model.StepParameters = "{";
        dialogs.Choice = SequenceUnsavedChoice.Save;
        Require(!await model.SelectSequenceAsync(b) && model.StepParameters == "{" && model.IsSequenceDirty, "Invalid pending parameters were lost on switch");
        model.SelectedRow = Row(model, "else");
        Require(model.SelectedRow?.Step?.Id == "child" && model.StepParameters == "{", "Invalid properties were lost on row selection");
        model.StepParameters = "{}"; model.StepName = "保存未应用输入"; model.SaveSequenceCommand.Execute(null);
        Require(!model.IsSequenceDirty && workspace.LoadSequences().Single(item => item.Id == a.Id).Steps.Single().Children.Single().Children.Single().Name == "保存未应用输入",
            "Save did not commit pending property input");
        model.VariableName = "bad name"; model.VariableDefault = "1";
        Require(!await model.PrepareToLeaveAsync() && model.VariableName == "bad name", "Invalid pending variable did not prevent leaving");
        dialogs.Choice = SequenceUnsavedChoice.Discard; Require(await model.PrepareToLeaveAsync(), "Discard could not leave invalid editor");
        await model.NewSequenceCommand.ExecuteAsync(null);
        var blocked = model.SelectedSequence!; var blockedPath = Path.Combine(workspace.Root, "sequences", blocked.Id + ".tsseq");
        Directory.CreateDirectory(blockedPath); dialogs.Choice = SequenceUnsavedChoice.Save;
        model.SaveSequenceCommand.Execute(null);
        Require(model.IsSequenceDirty && !await model.SelectSequenceAsync(b) && model.SelectedSequence == blocked, "Failed save was marked clean or allowed leaving");
        Directory.Delete(blockedPath); model.SaveSequenceCommand.Execute(null);
        Require(!model.IsSequenceDirty && File.Exists(blockedPath), "Document could not recover from save failure");
        model.NewSimulationValueCommand.Execute(null); model.SimulationChannel = "Voltage"; model.SimulationValue = "invalid";
        Require(!await model.PrepareToLeaveAsync(), "Invalid simulation form did not protect project switch");
        dialogs.Choice = SequenceUnsavedChoice.Discard; Require(await model.PrepareToLeaveAsync(), "Simulation discard failed");
        var current = model.ProjectPath;
        var damaged = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-documents", Guid.NewGuid().ToString("N")), "damaged");
        File.WriteAllText(Path.Combine(damaged.Root, "sequences", "bad.tsseq"), "{");
        try { model.OpenProject(damaged.Root); throw new InvalidOperationException("Malformed project opened"); }
        catch (JsonException) { }
        Require(model.ProjectPath == current && model.SelectedSequence == blocked, "Failed project load replaced the current document");
        var caseWorkspace = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-documents", Guid.NewGuid().ToString("N")), "case-sensitive-id");
        caseWorkspace.SaveSequence(new() { Id = "SEQUENCE_1", Name = "Existing", Steps = [Step("existing")] });
        var caseModel = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        caseModel.OpenProject(caseWorkspace.Root); await caseModel.NewSequenceCommand.ExecuteAsync(null);
        Require(caseModel.SelectedSequence?.Id == "sequence_2", "New ID collides with differently cased Windows filename");
        caseModel.SaveSequenceCommand.Execute(null);
        Require(caseWorkspace.LoadSequences().Count == 2 && caseWorkspace.LoadSequences().Any(item => item.Id == "SEQUENCE_1"),
            "Creating sequence overwrote an existing file with differently cased ID");
    }

    private static async Task WaitUntilAsync(Func<bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!ready() && DateTime.UtcNow < deadline) await Task.Delay(5);
        Require(ready(), "Timed out waiting for execution state");
    }
    private static async Task VerifyRunLocksAsync()
    {
        var (model, dialogs, workspace) = Fixture();
        var run = new SequenceDefinition { Id = "run", Name = "运行保护", Steps = [new() { Id = "wait", Kind = "wait", Name = "WAIT", Params = new() { ["timeout_ms"] = Value(5000) } }],
            CleanupSteps = [new() { Id = "finish", Kind = "wait", Name = "cleanup", Params = new() { ["timeout_ms"] = Value(250) } }] };
        workspace.SaveSequence(run);
        model.OpenProject(workspace.Root); await model.SelectSequenceAsync(model.Sequences.Single(item => item.Id == run.Id));
        var before = Snapshot(model.SelectedSequence!); var other = model.Sequences.First(item => item.Id == "a");
        var task = model.RunSequenceCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => model.ExecutionState == "RUNNING");
        AssertLocked();
        await model.PauseResumeCommand.ExecuteAsync(null); Require(model.ExecutionState == "PAUSED", "Pause failed"); AssertLocked();
        dialogs.Stop = false; Require(!await model.PrepareToLeaveAsync() && model.IsExecutionBusy, "Cancel close stopped a paused run");
        await model.PauseResumeCommand.ExecuteAsync(null);
        var stop = model.StopSequenceCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => model.ExecutionState == "STOPPED");
        Require(model.IsExecutionBusy && !stop.IsCompleted, "Cleanup unlocked editing before finishing"); AssertLocked();
        await stop; await task;
        Require(!model.IsExecutionBusy && model.CanEditSequence && model.RunLogs.Any(item => item.Message.Contains("cleanup")), "Cleanup did not finish or release editing");
        var reports = Directory.GetFiles(Path.Combine(workspace.Root, "reports"), "*", SearchOption.AllDirectories);
        Require(reports.Length > 0, "Stopped run did not save its result in the original project");
        task = model.RunSequenceCommand.ExecuteAsync(null); await WaitUntilAsync(() => model.ExecutionState == "RUNNING");
        dialogs.Stop = true;
        Require(await model.PrepareToLeaveAsync() && !model.IsExecutionBusy, "Stop-and-leave did not wait for Cleanup"); await task;
        var second = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-documents", Guid.NewGuid().ToString("N")), "next");
        model.OpenProject(second.Root); Require(model.ProjectPath == second.Root && model.Sequences.Count == 0, "Safe project switch failed");
        // Immediate stop must work even before the thread pool starts the engine.
        model.OpenProject(workspace.Root); await model.SelectSequenceAsync(model.Sequences.Single(item => item.Id == run.Id));
        task = model.RunSequenceCommand.ExecuteAsync(null); await model.StopSequenceCommand.ExecuteAsync(null); await task;
        Require(model.ExecutionState == "STOPPED" && !model.IsExecutionBusy, "Immediate stop raced engine startup");

        void AssertLocked()
        {
            Require(!model.CanEditSequence && !model.CanManageSequences && !model.CanEditSimulation && !model.CanUndoSequence, "Editing commands not locked");
            var count = model.Sequences.Count;
            model.AddStep("COMMENT"); model.DeleteSteps([Row(model, "wait")]); model.PasteSteps(); model.CutSteps([Row(model, "wait")]);
            model.MoveStep("wait", new("Setup", null, false, 0)); Row(model, "wait").Enabled = false;
            model.NewVariableCommand.Execute(null); model.ApplyVariableCommand.Execute(null); model.DeleteVariableCommand.Execute(null);
            model.NewSequenceCommand.Execute(null); model.SaveSequenceCommand.Execute(null);
            model.NewSimulationValueCommand.Execute(null); model.ApplySimulationValueCommand.Execute(null); model.DeleteSimulationValueCommand.Execute(null);
            Require(Snapshot(model.SelectedSequence!) == before && model.Sequences.Count == count, "Programmatic mutation bypassed running lock");
            Require(!model.SelectSequenceAsync(other).GetAwaiter().GetResult(), "Sequence switched while running");
            try { model.OpenProject(workspace.Root); throw new Exception("Project switch bypassed running lock"); }
            catch (InvalidOperationException) { }
        }
    }
}
