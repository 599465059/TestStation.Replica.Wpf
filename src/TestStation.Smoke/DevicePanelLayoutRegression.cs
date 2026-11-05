using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestStation.App.Devices.HiokiPowerAnalyzer.ViewModels;
using TestStation.App.Devices.HiokiPowerAnalyzer.Views;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
using TestStation.App.Devices.ItechPowerSupply.Views;
using TestStation.App.Devices.Reference;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Application.Resources;
using TestStation.Infrastructure.Devices.HiokiPowerAnalyzer;
using TestStation.Infrastructure.Devices.ItechPowerSupply;
using TestStation.Infrastructure.Devices.Reference;
using TestStation.Infrastructure.Transports;
using TestStation.Infrastructure.Resources;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class DevicePanelLayoutRegression
{
    public static void Verify(ThemeService theme)
    {
        var folder = Path.Combine(Path.GetTempPath(), "device-panel-layout", Guid.NewGuid().ToString("N"));
        var catalog = new ResourceCatalogService(new ResourceCatalogRepository());
        catalog.OpenProject(folder);
        using var resources = new ResourceManager(catalog);
        var profiles = new ItechPowerSupplyProfileStore(Path.Combine(folder, "family.json"));
        var factory = new TransportSessionFactory();
        var power = new PowerSupplyDeviceService(catalog, resources, factory, profiles);
        var hioki = new HiokiPowerAnalyzerDeviceService(catalog, resources, factory);
        var reference = new ReferenceScpiDeviceService(catalog, resources, factory);
        using var runtime = new SimulatedChannelRuntimeCoordinator(new SimulatedPluginChannelProvider());
        var dialogs = new TestSequenceDialogs();
        var first = power.AddDevice("直流电源 01");
        var second = power.AddDevice("直流电源 02");
        var meter = hioki.AddDevice("功率分析仪 01");
        var family = reference.Families.Single(f => f.Id == "reference.scpi.IT6000");
        var instrument = family.Add("参考电源 01");
        family.Update(instrument.Id, instrument.Name, "IT6005", "");
        var p1 = new ItechPowerSupplyViewModel(runtime, power, profiles, resources, dialogs, pinnedDeviceId: first.Id);
        var p2 = new ItechPowerSupplyViewModel(runtime, power, profiles, resources, dialogs, pinnedDeviceId: second.Id);
        var h = new HiokiPowerAnalyzerViewModel(runtime, hioki, resources, dialogs, pinnedDeviceId: meter.Id);
        var r = new ReferenceScpiViewModel(family.Id, reference, runtime, resources, dialogs, instrument.Id);
        // Seed presentation values only; this layout fixture never opens a transport.
        foreach (var p in new[] { p1, p2 })
            foreach (var field in p.Measurements) field.Accept(new(field.Definition.Id, JsonSerializer.SerializeToElement(12.345), DateTimeOffset.Now));
        foreach (var field in h.Fields) field.Accept(new(field.DefinitionId, JsonSerializer.SerializeToElement(230.15), DateTimeOffset.Now));
        var views = new UserControl[] { new ItechPowerSupplyView(p1), new ItechPowerSupplyView(p2), new HiokiPowerAnalyzerView(h), new ReferenceScpiView(r) };
        var board = new Grid();
        for (var i = 0; i < 2; i++) { board.ColumnDefinitions.Add(new()); board.RowDefinitions.Add(new()); }
        var scrolls = new List<ScrollViewer>();
        for (var i = 0; i < views.Length; i++)
        {
            var scroll = new ScrollViewer { Content = views[i], HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            scrolls.Add(scroll);
            var card = new Border { Child = scroll, Margin = new Thickness(4), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            Grid.SetRow(card, i / 2); Grid.SetColumn(card, i % 2); board.Children.Add(card);
        }
        var window = new Window { Title = "设备操作面板布局短验收", Content = board, Width = 1000, Height = 1100 };
        try
        {
            window.Show(); Pump();
            foreach (var view in views)
            {
                var config = (Expander)view.FindName("ConnectionSettings");
                Require(!config.IsExpanded, "Device configuration occupies the operation area by default.");
                config.IsExpanded = true; Pump();
                foreach (var button in Find<Button>(view).Where(b => BindingOperations.GetBinding(b, Button.CommandProperty) is not null))
                    Require(button.Command is not null, $"Unresolved device command: {BindingOperations.GetBinding(button, Button.CommandProperty)?.Path.Path}");
                config.IsExpanded = false; Pump();
            }
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Teal); Pump();
                foreach (var scroll in scrolls)
                    Require(scroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible,
                        $"Device panel does not fit a {scroll.ViewportWidth:0}px viewport; extent={scroll.ExtentWidth:0}.");
                foreach (var view in views)
                {
                    var body = (FrameworkElement)view.FindName("OperationPanel");
                    Require(body.ActualHeight < 540, $"Compact operation body is too tall: {body.ActualHeight:0}px.");
                    Require(Find<TextBox>(view).Where(box => box.IsVisible).All(box => box.ActualHeight >= 28), "Input height was compressed.");
                }
                Capture(window, "compact-panels-" + mode);
            }
            Require(!power.ConnectionFor(first.Id).Connected && !hioki.ConnectionFor(meter.Id).Connected && !reference.StateFor(instrument.Id).Connected,
                "Layout/configuration interaction unexpectedly connected a device.");
            foreach (var view in views) ((Expander)view.FindName("ConnectionSettings")).IsExpanded = true;
            Pump();
            Capture(window, "compact-panels-configuration");
            p1.SelectedDevice = null; p2.SelectedDevice = null; h.SelectedDevice = null; r.SelectedDevice = null;
            foreach (var view in views) ((Expander)view.FindName("ConnectionSettings")).ClearValue(Expander.IsExpandedProperty);
            Pump();
            Require(views.All(view => ((Expander)view.FindName("ConnectionSettings")).IsExpanded),
                "An empty panel does not expose device creation/configuration.");
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Teal); Pump();
                Capture(window, "compact-panels-empty-" + mode);
            }
            Console.WriteLine("PASS: real ITECH/HIOKI/reference views in four narrow cards; collapsed configuration, resolved commands, readable inputs, light/dark/teal; no transport opened");
        }
        finally
        {
            window.Close();
            foreach (var view in views) ((IDisposable)view).Dispose();
            power.DisposeAsync().AsTask().GetAwaiter().GetResult();
            hioki.DisposeAsync().AsTask().GetAwaiter().GetResult();
            reference.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Find<T>(child)) yield return nested; }
    }
    private static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/workspace-layout")); Directory.CreateDirectory(folder);
        using var file = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(file);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
