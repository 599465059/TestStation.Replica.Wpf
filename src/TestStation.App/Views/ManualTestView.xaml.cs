using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Industrial.Controls;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Application;

namespace TestStation.App.Views;

/// <summary>One MDI surface hosts device views directly, in tabs or windows.</summary>
public partial class ManualTestView : UserControl, IDisposable
{
    public ManualTestViewModel Model { get; }
    public bool IsManipulating => false;
    private readonly IManualPluginViewRegistry _pluginRegistry;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly Dictionary<string, (Border Document, FrameworkElement View)> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _knownProviders = new(StringComparer.OrdinalIgnoreCase);
    private string _workspaceName = UiText.ManualTest;
    private bool _disposed;

    public ManualTestView(ManualTestViewModel model, IManualPluginViewRegistry pluginRegistry,
        IChannelRuntimeCoordinator runtime)
    {
        Model = model; _pluginRegistry = pluginRegistry; _runtime = runtime;
        InitializeComponent();
        DataContext = model;
        _pluginRegistry.Changed += OnPluginRegistryChanged;
        Loaded += (_, _) => ReconcilePluginViews();
        Workspace.ViewChanged += (_, _) => ViewToggleButton.Content = Workspace.View == WorkspaceView.Tabbed
            ? UiText.ManualWindowView : UiText.ManualTabbedView;
        Workspace.DocumentClosed += (_, document) =>
        {
            var entry = _documents.FirstOrDefault(p => p.Value.Document == document);
            if (entry.Key is not null)
            {
                if (entry.Value.View is IDisposable view) view.Dispose();
                _documents.Remove(entry.Key);
            }
            UpdateEmptyState();
        };
    }

    public void SetWorkspaceActive(bool active) { }
    public void SetWorkspaceName(string name) => _workspaceName = string.IsNullOrWhiteSpace(name) ? UiText.ManualTest : name;
    private void ToggleView_Click(object sender, RoutedEventArgs e) =>
        Workspace.View = Workspace.View == WorkspaceView.Tabbed ? WorkspaceView.Windows : WorkspaceView.Tabbed;
    private void Tile_Click(object sender, RoutedEventArgs e) => Workspace.Tile();
    private void Cascade_Click(object sender, RoutedEventArgs e) => Workspace.Cascade();

    private void AddPlugin_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = AddPluginButton, Placement = PlacementMode.Bottom };
        foreach (var provider in _pluginRegistry.Providers)
        {
            var item = new MenuItem { Header = provider.Name };
            item.Click += (_, _) => OpenPlugin(provider);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnPluginRegistryChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) ReconcilePluginViews();
        else Dispatcher.BeginInvoke(ReconcilePluginViews);
    }

    private void ReconcilePluginViews()
    {
        if (_disposed || !IsLoaded) return;
        var providers = _pluginRegistry.Providers;
        var enabled = providers.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _documents.Where(p => !enabled.Contains(p.Key.Split('#')[0])).ToArray())
            Workspace.CloseDocument(pair.Value.Document);
        _knownProviders.RemoveWhere(id => !enabled.Contains(id));
        var active = Workspace.ActiveDocument;
        foreach (var provider in providers)
            if (_knownProviders.Add(provider.Id)) OpenPlugin(provider);
        if (active is not null && _documents.Values.Any(p => p.Document == active)) Workspace.ActivateDocument(active);
        else if (_documents.Count > 0) Workspace.ActivateDocument(_documents.Values.First().Document);
        AddPluginButton.IsEnabled = providers.Count > 0;
        UpdateEmptyState();
    }

    private void OpenPlugin(IManualPluginViewProvider provider)
    {
        if (!_pluginRegistry.IsEnabled(provider.Id)) return;
        FrameworkElement view;
        try { view = provider.CreateView(new(Model.DocumentId, _workspaceName, _runtime)); }
        catch (Exception error)
        {
            view = new TextBlock { Text = UiText.Format("ManualPluginLoadFailed", error.Message),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20) };
            view.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
        }
        var scroll = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var document = Workspace.AddDocument(provider.Name, scroll);
        if (document.Child is Grid layout)
            foreach (var body in layout.Children.OfType<Border>().Where(b => Grid.GetRow(b) == 1)) body.Padding = new Thickness(0);
        _documents.Add(provider.Id + "#" + Guid.NewGuid().ToString("N"), (document, view));
        UpdateEmptyState();
    }

    private void UpdateEmptyState() => EmptyPluginState.Visibility = Workspace.DocumentCount == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pluginRegistry.Changed -= OnPluginRegistryChanged;
        foreach (var view in _documents.Values.Select(p => p.View).OfType<IDisposable>()) view.Dispose();
        _documents.Clear();
    }
}
