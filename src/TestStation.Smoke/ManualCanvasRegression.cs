using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application;
using TestStation.Domain;
using TestStation.Infrastructure;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class ManualCanvasRegression
{
    public static async Task VerifyWorkspaceModelAsync()
    {
        var root = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "canvas-workspaces", Guid.NewGuid().ToString("N")), "workspaces").Root;
        var editing = new ManualCanvasEditingService();
        var dialogs = new WorkspaceDialogs();
        var vm = new ManualWorkspaceViewModel(editing, new ManualCanvasRepository(editing), new ManualCanvasDocumentService(), new ManualWorkspaceRepository(), dialogs);
        vm.OpenProject(root);
        Require(vm.Documents.Count == 1 && vm.Documents[0].Name == "未命名", "Manual workspace migration did not name the main tab");
        var second = vm.AddDocument();
        vm.Rename(second, "电源测试");
        Require(vm.Documents.Count == 2 && second.Name == "电源测试", "Manual workspace creation or rename failed");
        Require(vm.Flush(), "Manual workspace catalog did not save");
        var entries = new ManualWorkspaceRepository().Load(root);
        Require(entries.Count == 2 && entries.Single(item => item.Id == second.Id).Name == "电源测试", "Manual workspace catalog did not persist names");
        await vm.CloseDocumentCommand.ExecuteAsync(second);
        Require(vm.Documents.Count == 1 && new ManualWorkspaceRepository().Load(root).Single(item => item.Id == second.Id).IsOpen == false, "Manual workspace close did not persist state");
        vm.ReopenDocumentCommand.Execute(new ManualWorkspaceEntry(second.Id, "电源测试", false));
        Require(vm.Documents.Count == 2 && vm.ActiveDocument?.Id == second.Id, "Manual workspace reopen failed");
        Console.WriteLine("PASS: manual multi-workspace creation, rename, persistence, close and reopen");
    }

    private sealed class WorkspaceDialogs : IManualWorkspaceDialogService
    {
        public Task<SequenceUnsavedChoice> AskUnsavedAsync(string name) => Task.FromResult(SequenceUnsavedChoice.Save);
        public Task<string?> RenameAsync(string name) => Task.FromResult<string?>(name);
    }
    public static ManualTestView CreateView(IThemeService? theme = null)
    {
        var editing = new ManualCanvasEditingService();
        var documentService = new ManualCanvasDocumentService();
        return new(new(editing, new ManualCanvasRepository(editing), documentService), new ManualPluginViewRegistry(), new SimulatedChannelRuntimeCoordinator(new SimulatedPluginChannelProvider()));
    }
    public static void VerifyModel()
    {
        var editing = new ManualCanvasEditingService();
        var original = new GridBounds(10, 10, 14, 4);
        Require(editing.Preview(original, CanvasGesture.Move, 23, -26, 16) == new GridBounds(11, 8, 14, 4), "Grid move rounding failed");
        Require(editing.Preview(original, CanvasGesture.Move, -999, -999, 16) == new GridBounds(0, 0, 14, 4), "Negative coordinates were allowed");
        Require(editing.Preview(original, CanvasGesture.West, 999, 0, 16) == new GridBounds(14, 10, 10, 4), "West resize failed to preserve right edge and minimum");
        Require(editing.Preview(original, CanvasGesture.North, 0, 999, 16) == new GridBounds(10, 12, 14, 2), "North resize failed to preserve bottom edge and minimum");
        Require(editing.Preview(original, CanvasGesture.SouthEast, 23, 26, 16) == new GridBounds(10, 10, 15, 6), "Corner resize rounding failed");
        var documentService = new ManualCanvasDocumentService();
        var document = new ManualCanvasDefinition();
        var serviceWidget = new ManualWidgetDefinition("service", "numeric", "服务测试", "V", new(0, 0, 10, 3));
        documentService.AddWidget(document, serviceWidget);
        Require(document.Widgets.Count == 1, "Canvas document service did not add a widget");
        Require(documentService.UpdateBounds(document, "service", new(2, 1, 12, 4)), "Canvas document service did not update bounds");
        Require(documentService.UpdateWidget(document, serviceWidget with { Name = "已更新" }) && document.Widgets[0].Name == "已更新", "Canvas document service did not replace a widget");
        Require(documentService.RemoveWidgets(document, ["service"]) == 1 && document.Widgets.Count == 0, "Canvas document service did not remove widgets");
        var root = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "canvas-tests", Guid.NewGuid().ToString("N")), "canvas").Root;
        var repository = new ManualCanvasRepository(editing);
        var legacyDocument = new ManualCanvasDefinition
        {
            GridSize = 16,
            Widgets = [new ManualWidgetDefinition("legacy", "numeric", "电压", "V", new(3, 3, 14, 4))]
        };
        repository.Save(root, legacyDocument);
        var loaded = repository.Load(root) ?? throw new InvalidOperationException("Legacy layout did not reload");
        Require(loaded.Widgets.Single().Id == "legacy", "Legacy layout compatibility load failed");
        var persistedBounds = loaded.Widgets.Single().Bounds;
        var movedBounds = editing.Preview(persistedBounds, CanvasGesture.Move, 50, 25, loaded.GridSize);
        var updated = new ManualCanvasDefinition
        {
            Version = loaded.Version,
            GridSize = loaded.GridSize,
            WidgetLibraryBaseline = loaded.WidgetLibraryBaseline,
            Widgets = [loaded.Widgets.Single() with { Bounds = movedBounds }]
        };
        repository.Save(root, updated);
        Require(repository.Load(root)!.Widgets.Single().Bounds == movedBounds, "Legacy layout integer bounds did not persist");

        // The current manual page is plugin based. A new project must not
        // silently create a canvas widget; the canvas model remains available
        // only for explicitly opened legacy layouts and compatibility commands.
        var model = new ManualTestViewModel(editing, repository, documentService);
        model.OpenProject(root);
        Require(model.Widgets.Count == 0, "New manual page created an implicit canvas widget");
        Require(model.AddWidgetCommand.CanExecute(null), "Manual canvas compatibility command is unavailable");
        model.AddWidgetAt("numeric", 3, 3);
        var widget = model.Widgets.Single();
        var bounds = widget.Bounds;
        model.Preview(widget, bounds, CanvasGesture.Move, 50, 25);
        model.CompleteGesture(widget, bounds, true);
        Require(widget.Bounds == bounds, "Cancelled compatibility gesture changed bounds");
        model.Preview(widget, bounds, CanvasGesture.Move, 50, 25);
        model.CompleteGesture(widget, bounds, false);
        var changedBounds = widget.Bounds;
        Require(changedBounds != bounds, "Compatibility gesture did not update in-memory bounds");
        model.IsDesignMode = false;
        Require(!model.AddWidgetCommand.CanExecute(null) && !model.DeleteWidgetCommand.CanExecute(null), "Run mode canvas editing was enabled");
        model.Preview(widget, changedBounds, CanvasGesture.Move, 300, 300);
        Require(widget.Bounds == changedBounds, "Run mode gesture changed compatibility bounds");

        var invalid = new ManualCanvasDefinition { Widgets = [new("a", "numeric", "Voltage", "V", new(0, 0, 1, 1))] };
        try { repository.Save(root, invalid); throw new Exception("Invalid layout was saved"); } catch (InvalidDataException) { }
        var path = Path.Combine(root, "layouts", "manual", "canvas.json");
        File.WriteAllText(path, JsonSerializer.Serialize(invalid));
        try { repository.Load(root); throw new Exception("Corrupt layout was accepted"); } catch (InvalidDataException) { }
        Console.WriteLine("PASS: canvas geometry, legacy layout round trip, explicit compatibility editing, run lock and corrupt-file protection");
    }
    public static void RunUi()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { VerifyUi(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyWorkspaceWindow(System.Windows.Application app, ThemeService theme)
    {
        var main = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        main.CreateProject(Path.Combine(Path.GetTempPath(), "canvas-workspace-ui", Guid.NewGuid().ToString("N")), "canvas");
        var editing = new ManualCanvasEditingService();
        var workspaceModel = new ManualWorkspaceViewModel(editing, new ManualCanvasRepository(editing), new ManualCanvasDocumentService(), new ManualWorkspaceRepository(), new WorkspaceDialogs());
        var configuration = new ManualWidgetConfigurationAdapter();
        var workspaceView = new ManualWorkspaceView(workspaceModel, new ManualInstrumentFactory(configuration), configuration, new ManualPluginViewRegistry(), new SimulatedChannelRuntimeCoordinator(new SimulatedPluginChannelProvider()));
        var window = new MainWindow(main, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), new ProtocolDialogService(), CreateView());
        try
        {
            window.Show(); window.Activate(); Pump();
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            NavigationSmokeHelper.Invoke(window, "手动测试"); Pump();
            Require(workspaceModel.Documents.Count == 1 && workspaceView.IsVisible, "Manual workspace page did not open its first document");
            var newButton = Find<Button>(workspaceView).Single(button => AutomationProperties.GetAutomationId(button) == "NewManualWorkspace");
            NativePointer.Click(newButton.PointToScreen(new Point(newButton.ActualWidth / 2, newButton.ActualHeight / 2))); Pump();
            Require(workspaceModel.Documents.Count == 2, "Manual workspace did not create a second tab");
            workspaceModel.Rename(workspaceModel.Documents[1], "电源测试"); Pump();
            Require(workspaceModel.Documents[1].Title == "电源测试", "Manual workspace tab rename did not update the title");
            workspaceModel.ActiveDocument = workspaceModel.Documents[1]; Pump();
            Require(workspaceModel.Documents[1].Model.Widgets.Count > 0, "Manual workspace tab did not activate its independent canvas");
            NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
            Require(!workspaceView.IsVisible, "Manual workspace stayed visible after navigation");
            NavigationSmokeHelper.Invoke(window, "手动测试"); Pump();
            Require(workspaceModel.Documents.Count == 2 && workspaceView.IsVisible, "Manual workspace tabs were recreated on navigation");
            Console.WriteLine("PASS: manual workspace tabs, independent documents, rename and navigation caching");
        }
        finally { window.Close(); Pump(); }
    }

    private static void VerifyUi()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        var theme = new ThemeService();
        var main = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        main.CreateProject(Path.Combine(Path.GetTempPath(), "canvas-ui", Guid.NewGuid().ToString("N")), "canvas");
        var view = CreateView(theme);
        var window = new MainWindow(main, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), new ProtocolDialogService(), view) { WindowState = WindowState.Maximized };
        try
        {
            window.Show(); window.Activate(); Pump();
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            window.UpdateLayout();
            NavigationSmokeHelper.Invoke(window, "手动测试"); Pump();
            Require(view.IsVisible && Window.GetWindow(view) == window, "Canvas was not embedded in manual-test page");
            // Let navigation and maximize animations settle before calculating
            // physical pointer coordinates for native mouse input.
            for (var i = 0; i < 4; i++) Pump();
            var surface = (Grid)view.FindName("CanvasSurface");
            var items = (ItemsControl)view.FindName("CanvasItems");
            var scroll = (ScrollViewer)view.FindName("CanvasScroll");
            var widget = view.Model.Widgets.Single(); var initial = widget.Bounds;
            var presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(widget);
            NativePointer.Drag(presenter.PointToScreen(new Point(90, 16)), presenter.PointToScreen(new Point(141, 43)), () =>
            { Require(view.IsManipulating, "Canvas did not enter dragging state"); Capture(window, "dragging"); }); Pump();
            Require(widget.Bounds.Column > initial.Column && widget.Bounds.Row > initial.Row &&
                (widget.Bounds.Column - initial.Column) >= 1 && (widget.Bounds.Row - initial.Row) >= 1,
                $"Actual mouse move did not snap: initial {initial}, actual {widget.Bounds}");
            Require(!view.IsManipulating && ReferenceEquals(view.Model.Selected, widget), "Selected widget was lost after mouse release");
            foreach (var gesture in Enum.GetValues<CanvasGesture>().Where(g => g != CanvasGesture.Move))
            {
                var before = widget.Bounds; view.Model.Select(widget); Pump(); view.UpdateLayout();
                presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(widget);
                var handle = Find<Border>(presenter).Single(b => b.Tag?.ToString() == gesture.ToString());
                var start = handle.PointToScreen(new Point(4, 4));
                NativePointer.Drag(start, start + new Vector(27, 19)); Pump(); view.UpdateLayout();
                Require(widget.Bounds == new ManualCanvasEditingService().Preview(before, gesture, 27, 19, 16), $"Actual resize {gesture} failed");
                Require(ReferenceEquals(view.Model.Selected, widget), "Selected widget was lost after resize release");
            }
            presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(widget);
            var cancelBounds = widget.Bounds;
            NativePointer.Drag(presenter.PointToScreen(new Point(90, 20)), presenter.PointToScreen(new Point(140, 70)), () => NativePointer.Keys(0x1B)); Pump();
            Require(widget.Bounds == cancelBounds && !view.IsManipulating, "Escape failed to cancel drag");
            view.Model.IsDesignMode = false; Pump();
            Require(!((FrameworkElement)view.FindName("WidgetPropertyPanel")).IsEnabled, "Run mode left canvas properties editable");
            presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(widget);
            NativePointer.Drag(presenter.PointToScreen(new Point(90, 20)), presenter.PointToScreen(new Point(140, 70))); Pump();
            Require(widget.Bounds == cancelBounds, "Run mode allowed canvas edits");
            view.Model.IsDesignMode = true;
            Require(((FrameworkElement)view.FindName("WidgetPropertyPanel")).IsEnabled, "Design mode did not restore canvas properties");
            foreach (var mode in Enum.GetValues<ThemeMode>())
                foreach (var accent in new[] { AccentPalette.Blue, AccentPalette.Teal })
                {
                    theme.Apply(mode, accent); Pump();
                    Require(((SolidColorBrush)view.Background).Color == ((SolidColorBrush)app.Resources["ContentBrush"]).Color, "Canvas did not follow theme");
                    Capture(window, $"{mode.ToString().ToLowerInvariant()}-{accent.ToString().ToLowerInvariant()}");
                }
            view.Model.Select(widget); view.Model.Nudge(1000, 800); Pump();
            Require(scroll.ScrollableWidth > 0 && scroll.ScrollableHeight > 0, "Canvas did not overflow with scrolling");
            Require(widget.Width >= 160 && widget.Height >= 32, "Widget was compressed to fit viewport");
            var reloaded = CreateView(); reloaded.Model.OpenProject(main.ProjectPath!);
            Require(reloaded.Model.Widgets.Single().Bounds == widget.Bounds, "Actual drag/resize layout was not restored");
            IndustrialCanvasRegression.Verify(view, window);
            NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
            Require(!view.IsVisible && !view.IsManipulating, "Manual page failed to detach");
            window.Close(); Pump();
            VerifyWorkspaceWindow(app, theme);
            Console.WriteLine("PASS: embedded manual page, native move and all 8 resize handles, transient grid, Escape, run lock, 4 theme/accent combinations, scrolling and saved reload");
        }
        catch { Capture(window, "failure"); throw; }
        finally { window.Close(); Pump(); app.Shutdown(); }
    }
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match;
            foreach (var item in Find<T>(child)) yield return item;
        }
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts/ui"); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, $"manual-canvas-{name}.png")); encoder.Save(file);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
