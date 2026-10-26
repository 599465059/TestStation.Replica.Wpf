using System.Windows;
using System.Windows.Controls;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Application;

namespace TestStation.App.Views;

/// <summary>
/// Manual test page shell. Plugin views will be hosted here; canvas editing is
/// intentionally not part of the current product surface.
/// </summary>
public partial class ManualTestView : UserControl, IDisposable
{
    public ManualTestViewModel Model { get; }
    public bool IsManipulating => false;
    private readonly IManualPluginViewRegistry? _pluginRegistry;
    private readonly IChannelRuntimeCoordinator? _runtime;
    private readonly List<FrameworkElement> _pluginViews = [];
    private string _workspaceName;
    private bool _tilePlugins;

    public ManualTestView(ManualTestViewModel model, ManualInstrumentFactory? unusedInstrumentFactory = null,
        ManualWidgetConfigurationAdapter? unusedConfiguration = null, IThemeService? theme = null,
        IManualPluginViewRegistry? pluginRegistry = null, string? workspaceName = null,
        IChannelRuntimeCoordinator? runtime = null)
    {
        Model = model; _pluginRegistry = pluginRegistry; _runtime = runtime; _workspaceName = workspaceName ?? "手动测试";
        InitializeComponent();
        DataContext = model;
        if (_pluginRegistry is not null) _pluginRegistry.Changed += OnPluginRegistryChanged;
        Loaded += (_, _) => RenderPluginViews();
    }

    public void SetWorkspaceActive(bool active) { }
    public void SetWorkspaceName(string name) => _workspaceName = string.IsNullOrWhiteSpace(name) ? "手动测试" : name;

    private void PluginTabs_Click(object sender, RoutedEventArgs e) { _tilePlugins = false; RenderPluginViews(); }
    private void PluginTile_Click(object sender, RoutedEventArgs e) { _tilePlugins = true; RenderPluginViews(); }

    private void OnPluginRegistryChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) RenderPluginViews();
        else Dispatcher.BeginInvoke(RenderPluginViews);
    }

    private void RenderPluginViews()
    {
        if (!IsLoaded || PluginHost is null) return;
        foreach (var view in _pluginViews.OfType<IDisposable>()) view.Dispose();
        _pluginViews.Clear();
        PluginHost.Children.Clear();
        PluginTabsButton.Visibility = Visibility.Collapsed;
        PluginTileButton.Visibility = Visibility.Collapsed;
        var providers = _pluginRegistry?.Providers ?? [];
        if (providers.Count == 0)
        {
            PluginHost.Children.Add(EmptyPluginState);
            return;
        }

        PluginTabsButton.Visibility = Visibility.Visible;
        PluginTileButton.Visibility = Visibility.Visible;
        var tabs = new TabControl { Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"), BorderThickness = new Thickness(0) };
        var tile = new Grid { Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush") };
        var columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(providers.Count)), 1, 3);
        var rows = (int)Math.Ceiling(providers.Count / (double)columns);
        for (var column = 0; column < columns; column++) tile.ColumnDefinitions.Add(new ColumnDefinition());
        for (var row = 0; row < rows; row++) tile.RowDefinitions.Add(new RowDefinition());
        foreach (var provider in providers)
        {
            try
            {
                if (_runtime is null) throw new InvalidOperationException("通道运行协调器未注册");
                var context = new ManualPluginViewContext(Model.DocumentId, _workspaceName, _runtime);
                var tabView = provider.CreateView(context);
                var tileView = provider.CreateView(context);
                _pluginViews.Add(tabView); _pluginViews.Add(tileView);
                tabs.Items.Add(new TabItem { Header = provider.Name, ToolTip = $"{provider.Name} v{provider.Version}", Content = tabView });
                var index = _pluginViews.Count / 2 - 1;
                var host = new Border { Child = tileView, Margin = new Thickness(6), BorderThickness = new Thickness(1),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush") };
                Grid.SetColumn(host, index % columns); Grid.SetRow(host, index / columns); tile.Children.Add(host);
            }
            catch (Exception error)
            {
                tabs.Items.Add(new TabItem { Header = provider.Name, Content = new TextBlock
                {
                    Text = $"插件界面加载失败：{error.Message}", TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(20), Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush")
                }});
            }
        }
        PluginHost.Children.Add(_tilePlugins ? tile : tabs);
    }

    public void Dispose()
    {
        if (_pluginRegistry is not null) _pluginRegistry.Changed -= OnPluginRegistryChanged;
        foreach (var view in _pluginViews.OfType<IDisposable>()) view.Dispose();
        _pluginViews.Clear();
    }
}
