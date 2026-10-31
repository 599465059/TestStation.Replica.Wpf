using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using Industrial.Controls;
using TestStation.App;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application;
using TestStation.Domain;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class DocumentUiRegression
{
    public static void Verify(ThemeService theme)
    {
        var workspace = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-doc-ui", Guid.NewGuid().ToString("N")), "project");
        workspace.SaveSequence(new() { Id = "a", Name = "A 序列", Steps = [new() { Id = "root", Kind = "comment", Name = "备注" }] });
        workspace.SaveSequence(new() { Id = "b", Name = "B 序列", Steps = [new() { Id = "other", Kind = "comment", Name = "其他步骤" }] });
        var dialogs = new ProtocolDialogService();
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: dialogs);
        model.OpenProject(workspace.Root);
        var protocols = ProtocolUiRegression.Create(); protocols.Pages.OpenProject(workspace.Root);
        var window = new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), dialogs, ManualCanvasRegression.CreateView()) { WindowState = WindowState.Maximized };
        var host = (DialogHost)window.FindName("AppDialogHost");
        try
        {
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            window.Show(); window.Activate(); Pump();
            NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
            var view = (SequenceWorkspace)window.FindName("SequenceView");
            var grid = (DataGrid)view.FindName("StepGrid");
            var list = (ListBox)view.FindName("SequenceList");
            var a = model.SelectedSequence!; var b = model.Sequences.Single(item => item.Id == "b");
            model.AddStep("COMMENT", new("Setup", null, false, 0)); Pump();
            Require(model.IsSequenceDirty && ((TextBlock)view.FindName("DocumentStatus")).Text == UiText.Unsaved, "Dirty status missing");
            Require(((DockingManager)view.FindName("DockManager")).Layout.Descendents().OfType<LayoutAnchorable>()
                .Single(panel => panel.ContentId == "Steps").Title == UiText.SequenceEditor + " *", "Dock title does not show document/dirty status");
            grid.Focus(); NativePointer.Keys(0x11, 0x5A); Pump();
            Require(a.SetupSteps.Count == 0 && !model.IsSequenceDirty, "Ctrl+Z did not undo the insertion");
            NativePointer.Keys(0x11, 0x59); Pump();
            Require(a.SetupSteps.Count == 1, "Ctrl+Y did not redo insertion");
            NativePointer.Keys(0x11, 0x53); Pump();
            Require(!model.IsSequenceDirty && workspace.LoadSequences().Single(item => item.Id == "a").SetupSteps.Count == 1, "Ctrl+S did not save");
            grid.SelectedItem = model.Rows.Single(row => row.RowType == "step" && row.Step?.Id == a.SetupSteps.Single().Id); Pump();
            var input = Find<TextBox>(view).Single(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "StepName");
            input.Focus(); input.SelectAll(); input.SelectedText = "待提交输入"; Pump();
            NativePointer.Keys(0x11, 0x5A); Pump();
            Require(a.SetupSteps.Count == 1 && !model.CanRedoSequence, "Textbox Ctrl+Z modified document history");
            input.Text = "保存未应用属性"; Pump(); NativePointer.Keys(0x11, 0x53); Pump();
            Require(workspace.LoadSequences().Single(item => item.Id == "a").SetupSteps.Single().Name == "保存未应用属性", "Ctrl+S ignored property form");

            Click((Button)view.FindName("RenameSequenceButton")); Require(host.IsOpen, "Rename did not use library DialogHost");
            Choose("PART_Cancel"); Require(a.Name == "A 序列", "Cancelled rename changed model");
            Click((Button)view.FindName("RenameSequenceButton"));
            ((TextBox)host.DialogContent!).Text = "已更名";
            Choose("PART_Primary"); Require(a.Name == "已更名" && model.IsSequenceDirty, "Rename dialog did not commit");
            Click((Button)view.FindName("UndoButton")); Require(a.Name == "A 序列", "Undo toolbar did not revert rename");
            Click((Button)view.FindName("RedoButton")); Require(a.Name == "已更名", "Redo toolbar did not restore rename");
            model.SaveSequenceCommand.Execute(null); Pump();
            Click((Button)view.FindName("DuplicateSequenceButton")); var copy = model.SelectedSequence!;
            Require(copy.Id != a.Id && model.IsSequenceDirty, "Copy toolbar did not create independent document");
            model.SaveSequenceCommand.Execute(null); Pump();
            Click((Button)view.FindName("DeleteSequenceButton")); Require(host.IsOpen, "Delete did not request confirmation");
            Choose("PART_Cancel"); Require(model.Sequences.Contains(copy), "Cancelled delete removed sequence");
            Click((Button)view.FindName("DeleteSequenceButton")); Choose("PART_Primary");
            Require(!model.Sequences.Contains(copy), "Confirmed delete retained sequence");
            if (model.SelectedSequence != a) { list.SelectedItem = a; Pump(); }

            model.SequenceName = "未保存更名"; list.SelectedItem = b; Pump();
            Require(host.IsOpen && model.SelectedSequence == a, "Sequence switch did not protect dirty metadata");
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Teal); Pump();
                var text = host.DialogContent as TextBlock;
                Require(text?.Foreground == System.Windows.Application.Current.Resources["TextBrush"], "Unsaved prompt did not retain current theme");
                Require(Find<Button>(host).Single(button => button.Name == "PART_Cancel").IsVisible, "Dialog actions not visible");
            }
            Choose("PART_Cancel"); Require(model.SelectedSequence == a && model.SequenceName == "未保存更名", "Switch Cancel lost pending form");
            list.SelectedItem = b; Pump(); Choose("PART_Primary");
            Require(model.SelectedSequence == b && workspace.LoadSequences().Single(item => item.Id == "a").Name == "未保存更名",
                $"Switch Save did not persist: selected={model.SelectedSequence?.Id}, form={model.SequenceName}, status={model.Status}, saved={workspace.LoadSequences().Single(item => item.Id == "a").Name}");
            model.SequenceName = "放弃更名"; list.SelectedItem = a; Pump(); Choose("PART_Secondary");
            Require(model.SelectedSequence == a && b.Name == "B 序列", "Switch Discard did not restore saved model");

            // Invoke the shared shell entry point to avoid automating the system folder picker.
            var next = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-doc-ui", Guid.NewGuid().ToString("N")), "next");
            model.SequenceDescription = "阻止切换";
            var method = typeof(MainWindow).GetMethod("OpenProject", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var open = (Task)method.Invoke(window, [next.Root])!; Pump(); Choose("PART_Cancel"); Wait(open);
            Require(model.ProjectPath == workspace.Root, "Project switch Cancel changed workspace");
            open = (Task)method.Invoke(window, [next.Root])!; Pump(); Choose("PART_Primary"); Wait(open);
            Require(model.ProjectPath == next.Root && workspace.LoadSequences().Single(item => item.Id == "a").Description == "阻止切换", "Project switch Save lost metadata");
            open = (Task)method.Invoke(window, [workspace.Root])!; Wait(open);
            NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
            a = model.SelectedSequence!; model.SequenceDescription = "关闭前保存";
            NativePointer.Keys(0x12, 0x73); Pump();
            Require(window.IsVisible && host.IsOpen, "Alt+F4 bypassed unsaved protection");
            Choose("PART_Cancel"); Require(window.IsVisible && model.IsSequenceDirty, "Close Cancel lost changes");
            Click((Button)window.FindName("CloseWindowButton"));
            Choose("PART_Primary");
            for (var attempt = 0; attempt < 20 && window.IsVisible; attempt++) Pump();
            Require(!window.IsVisible && workspace.LoadSequences().Single(item => item.Id == a.Id).Description == "关闭前保存", "Titlebar close Save failed");
            Console.WriteLine("PASS: actual Ctrl+Z/Y/S, native textbox undo, library rename/delete/unsaved dialogs, project switch, Alt+F4/close cancellation and save");
        }
        finally
        {
            if (host.IsOpen) host.Close(Industrial.Controls.DialogResult.Secondary);
            if (window.IsVisible) { model.SaveSequenceCommand.Execute(null); window.Close(); Pump(); }
        }

        void Choose(string name)
        {
            Require(host.IsOpen, $"Expected dialog before choosing {name}");
            Click(Find<Button>(host).Single(button => button.Name == name));
            for (var attempt = 0; attempt < 10 && (model.DeleteSequenceCommand.IsRunning || model.RenameSequenceCommand.IsRunning || model.DuplicateSequenceCommand.IsRunning); attempt++) Pump();
            Pump();
        }
    }
    public static void VerifyRunningClose(ThemeService theme)
    {
        var workspace = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-run-close", Guid.NewGuid().ToString("N")), "project");
        workspace.SaveSequence(new() { Id = "run", Name = "关闭保护", Steps = [WaitStep("main", 5000)], CleanupSteps = [WaitStep("cleanup", 800)] });
        var dialogs = new ProtocolDialogService();
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: dialogs);
        model.OpenProject(workspace.Root);
        var protocols = ProtocolUiRegression.Create(); protocols.Pages.OpenProject(workspace.Root);
        var window = new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), dialogs, ManualCanvasRegression.CreateView()) { WindowState = WindowState.Maximized };
        var host = (DialogHost)window.FindName("AppDialogHost");
        try
        {
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            window.Show(); window.Activate(); Pump();
            NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
            var view = (SequenceWorkspace)window.FindName("SequenceView");
            Click((Button)view.FindName("RunButton"));
            Require(model.IsExecutionBusy, "Run did not start");
            Click((Button)view.FindName("PauseButton"));
            Require(model.ExecutionState == "PAUSED", "Run did not pause");
            Click((Button)window.FindName("CloseWindowButton"));
            Require(host.IsOpen && Find<TextBlock>(host).Any(text => text.Text == UiText.ExecutionInProgress), "Closing paused execution did not request stop");
            Click(Find<Button>(host).Single(button => button.Name == "PART_Cancel"));
            Require(window.IsVisible && model.ExecutionState == "PAUSED", "Cancelled close stopped paused execution");
            Click((Button)window.FindName("CloseWindowButton"));
            Click(Find<Button>(host).Single(button => button.Name == "PART_Primary"));
            Require(window.IsVisible && model.IsExecutionBusy && !((Button)view.FindName("UndoButton")).IsEnabled,
                "Close terminated or unlocked the window before Cleanup finished");
            for (var attempt = 0; attempt < 100 && window.IsVisible; attempt++) Pump();
            Require(!window.IsVisible && !model.IsExecutionBusy && model.ExecutionState == "STOPPED", "Close did not wait for stop/Cleanup");
            Require(Directory.GetFiles(Path.Combine(workspace.Root, "reports"), "*", SearchOption.AllDirectories).Length > 0,
                "Closing execution lost its report");
            Console.WriteLine("PASS: actual paused-run close Cancel; stop-and-close waits for Cleanup and report persistence");
        }
        finally
        {
            if (host.IsOpen) host.Close(Industrial.Controls.DialogResult.Close);
            if (model.IsExecutionBusy) Wait(model.StopSequenceCommand.ExecuteAsync(null));
            if (window.IsVisible) { window.Close(); Pump(); }
        }
        static SequenceStep WaitStep(string id, int duration) => new() { Id = id, Name = id, Kind = "wait",
            Params = new() { ["timeout_ms"] = JsonSerializer.SerializeToElement(duration) } };
    }
    private static void Wait(Task task)
    {
        for (var attempt = 0; attempt < 100 && !task.IsCompleted; attempt++) Pump();
        Require(task.IsCompleted, "Shell operation did not complete"); task.GetAwaiter().GetResult();
    }
    private static void Click(Button button) { NativePointer.Click(button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2))); Pump(); }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
