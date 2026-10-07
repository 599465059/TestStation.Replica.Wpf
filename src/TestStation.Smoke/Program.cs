using System.IO;
using System.Text.Json;
using TestStation.App.ViewModels;
using TestStation.Application;
using TestStation.App.Services;
using TestStation.Smoke;
using TestStation.Domain;

if (args.Contains("--canvas-model"))
{
    ManualCanvasRegression.VerifyModel();
    return;
}
if (args.Contains("--ui-canvas"))
{
    try { ManualCanvasRegression.RunUi(); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}
if (args.Contains("--ui") || args.Contains("--ui-documents"))
{
    try { UiRegression.Run(args.Contains("--ui-documents")); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    return;
}
EditorRegression.Run();
ManualCanvasRegression.VerifyModel();
await ManualCanvasRegression.VerifyWorkspaceModelAsync();
DocumentRegression.Run();
ProtocolRegression.Run();
ResourceRegression.Run();
await DriverRegression.RunAsync();
var root = Path.Combine(Path.GetTempPath(), "test-station-wpf-smoke", Guid.NewGuid().ToString("N"));
var workspace = ProjectWorkspace.Create(root, "sample");
var sequence = new SequenceDefinition
{
    Id = "nested_case",
    Name = "Nested case",
    SetupSteps = [new SequenceStep { Id = "setup", Kind = "initialize", Name = "INITIALIZE" }],
    Steps = [new SequenceStep
    {
        Id = "condition", Kind = "if", Name = "IF",
        Children = [new SequenceStep { Id = "read", Kind = "read_value", Name = "GET VALUE" }],
        ElseChildren = [new SequenceStep { Id = "loop", Kind = "loop", Name = "FOR",
            Children = [new SequenceStep { Id = "wait", Kind = "wait", Name = "WAIT" }] }]
    }],
    CleanupSteps = [new SequenceStep { Id = "cleanup", Kind = "comment", Name = "COMMENT" }]
};
workspace.SaveSequence(sequence);

var path = Path.Combine(workspace.Root, "sequences", "nested_case.tsseq");
using (var document = JsonDocument.Parse(File.ReadAllText(path)))
{
    if (document.RootElement.GetProperty("format").GetString() != "test-station-sequence")
        throw new Exception("Sequence format does not match the Python project contract.");
    if (!document.RootElement.TryGetProperty("sequence", out _))
        throw new Exception("Sequence wrapper is missing.");
}
var reopened = ProjectWorkspace.Open(workspace.Root);
var restored = reopened.LoadSequences().Single();
if (restored.Steps[0].ElseChildren[0].Children[0].Id != "wait" ||
    restored.SetupSteps.Single().Id != "setup" || restored.CleanupSteps.Single().Id != "cleanup")
    throw new Exception("Nested sequence round trip failed.");

var editable = new SequenceDefinition { Id = "editor_case", Name = "Editor case" };
var condition = SequenceEditor.Insert(editable, "IF", new SequenceInsertTarget("Main", null, false, 0));
var loop = SequenceEditor.Insert(editable, "FOR", new SequenceInsertTarget("Main", condition.Id, false, 0));
var read = SequenceEditor.Insert(editable, "GET VALUE", new SequenceInsertTarget("Main", loop.Id, false, 0));
var alternative = SequenceEditor.Insert(editable, "COMMENT", new SequenceInsertTarget("Main", condition.Id, true, 0));
SequenceEditor.Insert(editable, "SET VALUE", new SequenceInsertTarget("Setup", null, false, 0));
SequenceEditor.Insert(editable, "SEQUENCE CALL", new SequenceInsertTarget("Cleanup", null, false, 0));
if (loop.Kind != "loop" || read.Kind != "read_value" || editable.SetupSteps.Single().Kind != "set_value" ||
    editable.CleanupSteps.Single().Kind != "call_sequence" || condition.Children.Single() != loop)
    throw new Exception("Palette names do not map to the sequence contract.");
if (SequenceEditor.Move(editable, condition.Id, new SequenceInsertTarget("Main", loop.Id, false, 0)))
    throw new Exception("A parent step moved into its own descendant.");
if (!SequenceEditor.Move(editable, loop.Id, new SequenceInsertTarget("Main", condition.Id, true, 0)) ||
    condition.Children.Count != 0 || condition.ElseChildren[0] != loop || condition.ElseChildren[1] != alternative)
    throw new Exception("Moving a nested FOR into ELSE failed.");
workspace.SaveSequence(editable);
var edited = ProjectWorkspace.Open(workspace.Root).LoadSequences().Single(item => item.Id == editable.Id);
if (edited.Steps.Single().ElseChildren[0].Children.Single().Kind != "read_value")
    throw new Exception("Edited branches did not survive persistence.");

var viewModel = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
viewModel.OpenProject(workspace.Root);
viewModel.SelectSequence(viewModel.Sequences.Single(item => item.Id == editable.Id));
var readRow = viewModel.Rows.Single(row => row.Step?.Id == read.Id);
viewModel.SelectedRow = readRow;
if (viewModel.StepFields.Select(field => field.Key).SequenceEqual(["channel_id", "timeout_ms"]) is false)
    throw new Exception("Read step did not expose its specific fields.");
viewModel.StepFields.Single(field => field.Key == "channel_id").Value = "Power.Voltage";
viewModel.StepFields.Single(field => field.Key == "timeout_ms").Value = "-1";
viewModel.ApplyStepCommand.Execute(null);
if (!viewModel.Status.Contains("不小于 0") || readRow.Step!.Params.ContainsKey("timeout_ms"))
    throw new Exception("Invalid timeout changed the step.");
viewModel.StepFields.Single(field => field.Key == "timeout_ms").Value = "250";
viewModel.ApplyStepCommand.Execute(null);
viewModel.SaveSequenceCommand.Execute(null);
var savedRead = ProjectWorkspace.Open(workspace.Root).LoadSequences().Single(item => item.Id == editable.Id)
    .Steps.Single().ElseChildren[0].Children.Single();
if (savedRead.Params["channel_id"].GetString() != "Power.Voltage" || savedRead.Params["timeout_ms"].GetInt32() != 250)
    throw new Exception("Step fields did not persist with their correct types.");

viewModel.NewVariableCommand.Execute(null);
viewModel.VariableName = "limit";
viewModel.VariableType = "number";
viewModel.VariableScope = "local";
viewModel.VariableDefault = "12.5";
viewModel.ApplyVariableCommand.Execute(null);
viewModel.SaveSequenceCommand.Execute(null);
var savedVariable = ProjectWorkspace.Open(workspace.Root).LoadSequences().Single(item => item.Id == editable.Id).Variables.Single();
if (savedVariable.Name != "limit" || savedVariable.Default?.GetDouble() != 12.5)
    throw new Exception("Variable declaration did not persist.");

viewModel.NewSimulationValueCommand.Execute(null);
viewModel.SimulationChannel = "Power.Voltage";
viewModel.SimulationValue = "12.5";
viewModel.ApplySimulationValueCommand.Execute(null);
var simulation = new SimulationRepository(workspace.Root).Load();
if (simulation["Power.Voltage"].GetDouble() != 12.5)
    throw new Exception("Simulation channel did not persist.");
viewModel.SimulationValue = "{\"invalid\":true}";
viewModel.ApplySimulationValueCommand.Execute(null);
if (!viewModel.Status.Contains("模拟值必须") || new SimulationRepository(workspace.Root).Load()["Power.Voltage"].GetDouble() != 12.5)
    throw new Exception("Invalid simulation value changed the file.");

var runnable = new SequenceDefinition
{
    Id = "runnable_case",
    Name = "Runnable case",
    SetupSteps = [new SequenceStep { Id = "init", Kind = "initialize", Name = "INITIALIZE" }],
    Steps =
    [
        new SequenceStep { Id = "set", Kind = "set_value", Name = "SET VALUE", Params =
            new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("Power.Voltage"), ["value"] = JsonSerializer.SerializeToElement(12.5) } },
        new SequenceStep { Id = "read", Kind = "read_value", Name = "GET VALUE", Params =
            new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("Power.Voltage") } },
        new SequenceStep { Id = "check", Kind = "assert", Name = "ASSERT", Params =
            new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("Power.Voltage"), ["comparator"] = JsonSerializer.SerializeToElement("=="), ["expected"] = JsonSerializer.SerializeToElement(12.5) } },
        new SequenceStep { Id = "branch", Kind = "if", Name = "IF", Params =
            new Dictionary<string, JsonElement> { ["expression"] = JsonSerializer.SerializeToElement("${Power.Voltage} > 10") },
            Children = [new SequenceStep { Id = "then", Kind = "comment", Name = "COMMENT", Description = "then branch" }],
            ElseChildren = [new SequenceStep { Id = "else", Kind = "comment", Name = "COMMENT", Description = "else branch" }] },
        new SequenceStep { Id = "loop", Kind = "loop", Name = "FOR", Params =
            new Dictionary<string, JsonElement> { ["variable"] = JsonSerializer.SerializeToElement("i"), ["from"] = JsonSerializer.SerializeToElement(1), ["to"] = JsonSerializer.SerializeToElement(3), ["step"] = JsonSerializer.SerializeToElement(1) },
            Children = [new SequenceStep { Id = "loop_body", Kind = "comment", Name = "COMMENT", Description = "loop body" }] }
    ],
    CleanupSteps = [new SequenceStep { Id = "cleanup", Kind = "comment", Name = "COMMENT", Description = "cleanup" }]
};
var engine = new SequenceExecutionEngine();
var engineLogs = new List<SequenceLogEntry>();
var engineSteps = new List<StepRunResult>();
engine.LogRaised += engineLogs.Add;
engine.StepChanged += engineSteps.Add;
var run = await engine.RunAsync(runnable, simulationValues: new Dictionary<string, object?>());
if (run.Status != "completed" || engineSteps.All(step => step.StepId != "then") || engineSteps.Any(step => step.StepId == "else") ||
    engineSteps.Count(step => step.StepId == "loop_body") != 3 || engineSteps.All(step => step.StepId != "cleanup") ||
    !engineLogs.Any(log => log.Message.Contains("序列结束")))
    throw new Exception("Nested simulated sequence execution failed.");
var runRepository = new RunResultRepository(workspace.Root);
runRepository.Save(run);
if (runRepository.LoadAll().Count != 1 || runRepository.LoadAll()[0].GetProperty("format").GetString() != "test-station-sequence-run")
    throw new Exception("Sequence run result was not persisted.");
var storedRun = runRepository.LoadAll()[0].GetProperty("result");
if (storedRun.GetProperty("sequence_id").GetString() != runnable.Id ||
    storedRun.GetProperty("status").GetString() != "completed" ||
    storedRun.GetProperty("steps")[0].GetProperty("step_id").GetString() != "init" ||
    storedRun.GetProperty("steps")[0].GetProperty("sequence_id").GetString() != runnable.Id ||
    storedRun.GetProperty("steps")[0].GetProperty("started_at").GetString() is not { } startedAt ||
    DateTimeOffset.Parse(startedAt) > DateTimeOffset.Parse(storedRun.GetProperty("steps")[0].GetProperty("finished_at").GetString()!))
    throw new Exception("Run report does not match the project contract.");

var variablesCase = new SequenceDefinition
{
    Id = "variables_case", Name = "Variables case",
    Variables = [new SequenceVariable { Name = "threshold", Scope = "parameter", DataType = "number" }],
    SetupSteps = [new SequenceStep { Id = "setup_init", Kind = "initialize", Name = "Initialize" }],
    Steps = [new SequenceStep { Id = "setup_read", Kind = "read_value", Name = "Read", Params =
        new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("Power.Voltage") } },
        new SequenceStep { Id = "if", Kind = "if", Name = "IF", Params =
        new Dictionary<string, JsonElement> { ["expression"] = JsonSerializer.SerializeToElement("(${Power.Voltage} > threshold && 2 + 3 * 4 == 14) || false") },
        Children = [new SequenceStep { Id = "selected", Kind = "comment", Name = "Selected" }],
        ElseChildren = [new SequenceStep { Id = "not_selected", Kind = "comment", Name = "Not selected" }] }],
    CleanupSteps = [new SequenceStep { Id = "final", Kind = "comment", Name = "Final" }]
};
var variableEngine = new SequenceExecutionEngine();
var missingParameter = await variableEngine.RunAsync(variablesCase);
if (missingParameter.Status != "failed" || variableEngine.State != SequenceRunState.Failed ||
    missingParameter.Steps.Count != 0 || !missingParameter.Error.Contains("threshold"))
    throw new Exception("Missing parameter did not fail cleanly.");
var variableRun = await variableEngine.RunAsync(variablesCase,
    new Dictionary<string, object?> { ["threshold"] = 10 },
    new Dictionary<string, object?> { ["Power.Voltage"] = 12.5 });
if (variableRun.Status != "completed" || !variableRun.Values.ContainsKey("threshold") ||
    variableRun.Steps.All(step => step.StepId != "selected") ||
    variableRun.Steps.Any(step => step.StepId == "not_selected"))
    throw new Exception("Parameter or expression evaluation failed.");
if (!SequenceExpression.Evaluate("false && missing > 0 || true", new Dictionary<string, object?>()) ||
    SequenceExpression.Evaluate("!(true || false) && 1 < 2", new Dictionary<string, object?>()))
    throw new Exception("Expression precedence or short circuit failed.");

var missingChannel = new SequenceDefinition { Id = "missing_channel", Name = "Missing channel",
    Steps = [new SequenceStep { Id = "read", Kind = "read_value", Name = "Read", Params =
        new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("unknown") } }] };
var failedRead = await new SequenceExecutionEngine().RunAsync(missingChannel);
if (failedRead.Status != "failed" || !failedRead.Steps.Single().Message.Contains("未配置"))
    throw new Exception("Missing simulation channel was silently accepted.");

var cleanupCase = new SequenceDefinition { Id = "cleanup_case", Name = "Cleanup failure",
    Steps = [new SequenceStep { Id = "main", Kind = "comment", Name = "Main" }],
    CleanupSteps = [new SequenceStep { Id = "bad_cleanup", Kind = "read_value", Name = "Read", Params =
        new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("unknown") } }] };
var cleanupRun = await new SequenceExecutionEngine().RunAsync(cleanupCase);
if (cleanupRun.Status != "failed" || cleanupRun.Steps.Last().StepId != "bad_cleanup")
    throw new Exception("Cleanup failure did not affect run result.");

var continueCase = new SequenceDefinition { Id = "continue_case", Name = "Continue assertion",
    Steps = [new SequenceStep { Id = "read", Kind = "read_value", Name = "Read", Params =
        new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("voltage") } },
        new SequenceStep { Id = "assert", Kind = "assert", Name = "Assert", Params =
        new Dictionary<string, JsonElement> { ["channel_id"] = JsonSerializer.SerializeToElement("voltage"),
            ["expected"] = JsonSerializer.SerializeToElement(20), ["failure_action"] = JsonSerializer.SerializeToElement("CONTINUE") } },
        new SequenceStep { Id = "following", Kind = "comment", Name = "Following" }] };
var continued = await new SequenceExecutionEngine().RunAsync(continueCase,
    simulationValues: new Dictionary<string, object?> { ["voltage"] = 12.5 });
if (continued.Status != "completed" || continued.Steps.Single(step => step.StepId == "assert").Status != "failed" ||
    continued.Steps.Last().StepId != "following")
    throw new Exception("CONTINUE assertion semantics are wrong.");

var childSequence = new SequenceDefinition { Id = "child", Name = "Child",
    SetupSteps = [new SequenceStep { Id = "child_setup", Kind = "comment", Name = "Setup" }],
    Steps = [new SequenceStep { Id = "child_main", Kind = "comment", Name = "Main" }],
    CleanupSteps = [new SequenceStep { Id = "child_cleanup", Kind = "comment", Name = "Cleanup" }] };
var parentSequence = new SequenceDefinition { Id = "parent", Name = "Parent",
    Steps = [new SequenceStep { Id = "call", Kind = "call_sequence", Name = "Call", Params =
        new Dictionary<string, JsonElement> { ["sequence_id"] = JsonSerializer.SerializeToElement("child") } }] };
var callRun = await new SequenceExecutionEngine(sequenceResolver: id => id == "child" ? childSequence : null).RunAsync(parentSequence);
if (callRun.Status != "completed" || callRun.Steps.Count(step => step.SequenceId == "child") != 3 ||
    callRun.Steps.All(step => step.StepId != "child_cleanup"))
    throw new Exception("Called sequence did not execute its Cleanup section.");

workspace.SaveSequence(runnable);
var runViewModel = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
runViewModel.OpenProject(workspace.Root);
runViewModel.SelectSequence(runViewModel.Sequences.Single(item => item.Id == runnable.Id));
await runViewModel.RunSequenceCommand.ExecuteAsync(null);
if (runViewModel.ExecutionState != "PASSED" || !runViewModel.ExecutionSummary.Contains("completed") ||
    runViewModel.RunLogs.All(entry => !entry.Message.Contains("序列结束")) ||
    new RunResultRepository(workspace.Root).LoadAll().Count != 2)
    throw new Exception("ViewModel run command did not update logs and save a report.");

var pauseSequence = new SequenceDefinition
{
    Id = "pause_case", Name = "Pause case",
    Steps = [new SequenceStep { Id = "wait", Kind = "wait", Name = "WAIT", Params =
        new Dictionary<string, JsonElement> { ["timeout_ms"] = JsonSerializer.SerializeToElement(160) } }]
};
var pauseEngine = new SequenceExecutionEngine();
var pauseTask = pauseEngine.RunAsync(pauseSequence);
await Task.Delay(35);
await pauseEngine.PauseAsync();
if (pauseEngine.State != SequenceRunState.Paused) throw new Exception("Pause state was not entered.");
await Task.Delay(90);
if (pauseTask.IsCompleted) throw new Exception("Paused WAIT completed while paused.");
await pauseEngine.ResumeAsync();
var resumed = await pauseTask;
if (resumed.Status != "completed") throw new Exception("Paused sequence did not resume.");

var stopSequence = new SequenceDefinition
{
    Id = "stop_case", Name = "Stop case",
    Steps = [new SequenceStep { Id = "wait", Kind = "wait", Name = "WAIT", Params =
        new Dictionary<string, JsonElement> { ["timeout_ms"] = JsonSerializer.SerializeToElement(3000) } }]
};
var stopEngine = new SequenceExecutionEngine();
var stopTask = stopEngine.RunAsync(stopSequence);
await Task.Delay(35);
await stopEngine.StopAsync();
var stopped = await stopTask;
if (stopped.Status != "stopped" || stopEngine.State != SequenceRunState.Stopped)
    throw new Exception("Stopped sequence did not end as STOPPED.");

Console.WriteLine($"PASS: project, sequence editor, execution engine, variables, simulation values, and run reports ({workspace.Root})");
