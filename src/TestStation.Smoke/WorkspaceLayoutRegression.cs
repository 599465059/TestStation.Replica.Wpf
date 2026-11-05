using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock.Layout;
using Industrial.Controls;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class WorkspaceLayoutRegression
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Verify(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Verify()
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        var theme = new ThemeService();
        theme.Apply(ThemeMode.Light, AccentPalette.Teal);
        var folder = Path.Combine(Path.GetTempPath(), "test-station-layout", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var catalog = new TestCatalog(folder);
        var registry = new ManualPluginViewRegistry();
        var provider = new Provider("test.family"); registry.Register(provider);
        // Installed family count must not determine created page count.
        for (var i = 0; i < 100; i++) registry.Register(new Provider("unused." + i));
        for (var i = 1; i <= 6; i++) catalog.Catalog.Devices.Add(new() { Id = "D" + i, Name = "测试设备 " + i, FamilyId = provider.Id, ModelId = "Model" });
        var repository = new ManualPanelLayoutRepository();
        using var panels = new ManualPanelWorkspaceViewModel(catalog, repository, registry);
        var editing = new ManualCanvasEditingService();
        using var manual = new ManualTestView(new(editing, new ManualCanvasRepository(editing), new ManualCanvasDocumentService()), registry,
            new SimulatedChannelRuntimeCoordinator(new SimulatedPluginChannelProvider()), panels: panels);
        var window = new Window { Width = 1280, Height = 820, Content = manual };
        try
        {
            window.Show(); Pump();
            Require(provider.Created == 0 && panels.ActiveGroup!.Panels.Count == 0, "Installed families were eagerly instantiated");
            Capture(window, "manual-empty-light");
            // Exercise the searchable checkbox picker and add button, not only the view model.
            Invoke((Button)manual.FindName("AddPluginButton")); Pump();
            var choices = (ListBox)manual.FindName("DeviceChoices");
            Require(choices.Items.Count == 6, "Device picker does not list project instances");
            ((TextBox)manual.FindName("DeviceSearch")).Text = "D1"; Pump();
            Require(choices.Items.Count == 1, "Device search did not filter");
            var checkbox = Find<CheckBox>(choices).Single();
            checkbox.IsChecked = true; Pump();
            ((TextBox)manual.FindName("DeviceSearch")).Text = ""; Pump();
            Invoke(Find<Button>(((System.Windows.Controls.Primitives.Popup)manual.FindName("DevicePicker")).Child).Single());
            Pump();
            Require(panels.ActiveGroup!.Panels.Count == 1 && provider.Created == 1 && provider.Views[0].Selected == "D1", "Picker did not bind the selected instance");
            var firstGroup = panels.ActiveGroup;
            panels.AddDevices(panels.Choices.Where(d => d.DeviceId != "D1")); Pump();
            Require(panels.Groups.Count == 2 && panels.Groups[0].Panels.Count == 4 && panels.Groups[1].Panels.Count == 2, "Overflow was not split into groups of at most four");
            // Only the first group visited and the active overflow group exist so far.
            Require(provider.Created == 3, "Inactive group's pending panels were eagerly created");
            var secondSurface = (MdiWorkspace)((ContentControl)manual.FindName("WorkspaceHost")).Content;
            var secondViews = provider.Views.Where(v => v.IsLoaded).ToArray();
            panels.ActiveGroup = firstGroup; Pump();
            Require(provider.Created == 6 && secondViews.All(v => !v.IsLoaded && !v.IsVisible), "Group switch retained hidden views in the visual tree");
            var firstSurface = (MdiWorkspace)((ContentControl)manual.FindName("WorkspaceHost")).Content;
            panels.SetLayout("quad"); Pump();
            Require(firstSurface.DocumentCount == 4 && firstSurface.View == WorkspaceView.Windows && provider.Views.Count(v => v.IsVisible) == 4, "Four-pane mode does not show exactly four views");
            Capture(window, "manual-quad-light");
            theme.Apply(ThemeMode.Dark, AccentPalette.Purple); Pump();
            Capture(window, "manual-quad-dark");
            panels.SetLayout("columns"); Pump();
            panels.SetLayout("tabs"); Pump();
            Require(provider.Created == 6 && provider.Views.Count(v => v.IsVisible) == 1, "Tab mode rebuilt views or left hidden panels visible");
            panels.ActiveGroup = panels.Groups[1]; Pump();
            Require(ReferenceEquals(((ContentControl)manual.FindName("WorkspaceHost")).Content, secondSurface) && provider.Created == 6, "Revisiting a group did not reuse its workspace");
            panels.RenameGroup("测量分组"); Pump();
            var restored = repository.Load(folder);
            Require(restored.Groups.Count == 2 && restored.Groups[1].Name == "测量分组" && restored.Groups[0].Layout == "tabs", "Layout persistence lost groups or modes");
            var closed = secondSurface.ActiveDocument!;
            secondSurface.CloseDocument(closed); Pump();
            Require(panels.ActiveGroup!.Panels.Count == 1 && provider.Views.Count(v => v.Disposed) == 1, "Closing a panel did not release it and persist removal");
            registry.SetEnabled(provider.Id, false); Pump();
            Require(provider.Views.All(v => v.Disposed) && panels.Groups.Sum(g => g.Panels.Count) == 5, "Disabling a family leaked views or erased saved references");
            registry.SetEnabled(provider.Id, true); Pump();
            Require(provider.Created == 7, "Re-enabling instantiated inactive groups");
            var otherRoot = Path.Combine(folder, "other"); Directory.CreateDirectory(otherRoot);
            catalog.Switch(otherRoot); Pump();
            Require(panels.Groups.Count == 1 && panels.ActiveGroup!.Panels.Count == 0 && provider.Views.All(v => v.Disposed), "Project switch retained old panel views or references");
            catalog.Switch(folder); Pump();
            Require(panels.Groups.Count == 2 && panels.Groups.Sum(g => g.Panels.Count) == 5, "Project switch did not restore its own saved layout");
            Console.WriteLine("PASS: 101 installed families / zero default pages; instance search and multi-add; four-panel limit; lazy groups; view reuse; tab/column/grid; close/disable/project cleanup; atomic persistence");
            window.Content = null; manual.Dispose(); window.Close();
            VerifyVariables(theme);
            DevicePanelLayoutRegression.Verify(theme);
        }
        finally { window.Close(); app.Shutdown(); }
    }
    private static void VerifyVariables(ThemeService theme)
    {
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        model.CreateProject(Path.Combine(Path.GetTempPath(), "variable-layout", Guid.NewGuid().ToString("N")), "变量布局");
        model.NewSequenceCommand.Execute(null);
        var view = new SequenceWorkspace { DataContext = model };
        var window = new Window { Width = 1340, Height = 900, Content = view };
        try
        {
            window.Show(); Pump();
            var dock = (AvalonDock.DockingManager)view.FindName("DockManager");
            var variablePanel = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(p => p.ContentId == "Variables");
            variablePanel.IsActive = true; variablePanel.IsSelected = true;
            ((LayoutAnchorablePane)variablePanel.Parent).DockHeight = new GridLength(620); Pump();
            ((LayoutAnchorablePaneGroup)variablePanel.Parent.Parent).DockWidth = new GridLength(440);
            Pump();
            var table = (DataGrid)view.FindName("VariableGrid");
            var editor = (Expander)view.FindName("VariableEditor");
            Require(double.IsNaN(table.Height) && table.ActualHeight > 200, $"Variable table did not grow: {table.ActualHeight}");
            var initialCount = model.Variables.Count;
            var number = 0;
            foreach (var (type, value) in new[] { ("number", "1.5"), ("integer", "2"), ("boolean", "true"), ("text", "\"text\""), ("number[]", "[1,2]") })
            {
                model.SelectedVariable = null;
                model.VariableScope = "local"; model.VariableName = "value_" + number++; model.VariableType = type; model.VariableDefault = value;
                model.ApplyVariableCommand.Execute(null);
                Require(model.Variables.Any(v => v.Name == model.VariableName && v.DataType == type), $"Variable {type} was not applied: {model.Status}");
            }
            Require(model.Variables.Count == initialCount + 5, "Compact editors did not retain variable commands");
            var before = table.ActualHeight;
            editor.IsExpanded = false; Pump();
            Require(table.ActualHeight > before + 80, "Collapsing variable editor did not return space to the list");
            editor.IsExpanded = true; Pump();
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Teal); Pump();
                Require(Find<TextBox>(editor).Where(c => c.IsVisible).All(c => c.ActualHeight >= 28), "Compact inputs were compressed below readable height");
                Capture(window, "variables-" + mode);
            }
            Console.WriteLine("PASS: variable table stretches; compact editor commands; collapse returns list space; light/dark/teal layouts");
        }
        finally { window.Close(); }
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/workspace-layout")); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Find<T>(child)) yield return nested; }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Provider(string id) : IManualPluginViewProvider
    {
        public string Id => id; public string Name => id; public string Version => "1";
        public int Created { get; private set; }
        public List<TestPanel> Views { get; } = [];
        public FrameworkElement CreateView(ManualPluginViewContext context)
        { Created++; var view = new TestPanel(); Views.Add(view); return view; }
    }
    private sealed class TestPanel : UserControl, IDevicePanelSelection, IDisposable
    {
        public string? Selected { get; private set; }
        public bool Disposed { get; private set; }
        public void SelectDevice(string id) { Selected = id; Content = new TextBlock { Text = "设备实例 " + id, Margin = new Thickness(20) }; }
        public void Dispose() => Disposed = true;
    }
    private sealed class TestCatalog(string root) : IResourceCatalogService
    {
        public event EventHandler? Changed;
        public event EventHandler<ResourceCatalogChangingEventArgs>? Changing;
        public string? ProjectRoot { get; private set; } = root;
        public ResourceCatalog Catalog { get; } = new();
        public IReadOnlyList<string> Errors => [];
        public void OpenProject(string next) => Switch(next);
        public void Switch(string next) { Changing?.Invoke(this, new(Catalog, next)); ProjectRoot = next; Changed?.Invoke(this, EventArgs.Empty); }
        public void Save(ResourceCatalog catalog) => Changed?.Invoke(this, EventArgs.Empty);
    }
}
