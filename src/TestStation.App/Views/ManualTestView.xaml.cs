using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Industrial.Controls;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Application;

namespace TestStation.App.Views;

/// <summary>Only the active group's published MDI surface is attached to the visual tree.</summary>
public partial class ManualTestView : UserControl, IDisposable
{
    private sealed record PanelEntry(ManualPanelReference Reference, Border Document, FrameworkElement View);
    private sealed class GroupSurface(MdiWorkspace workspace)
    {
        public MdiWorkspace Workspace { get; } = workspace;
        public List<PanelEntry> Panels { get; } = [];
    }
    private sealed class PickerRow(ManualDeviceChoice choice) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        public ManualDeviceChoice Choice { get; } = choice;
        public string Caption => Choice.Caption;
        private bool _picked;
        public bool Picked { get => _picked; set => SetProperty(ref _picked, value); }
    }
    public ManualTestViewModel Model { get; }
    public ManualPanelWorkspaceViewModel? Panels { get; }
    public bool IsManipulating => false;
    private readonly IManualPluginViewRegistry _registry;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly IResourceEditorService? _resourceEditor;
    private readonly ISequenceDialogService? _dialogs;
    private readonly Dictionary<string, GroupSurface> _surfaces = [];
    private List<PickerRow> _pickerRows = [];
    private string _workspaceName = UiText.ManualTest;
    private bool _disposed, _reconciling, _layoutQueued, _applyingLayout;
    public ManualTestView(ManualTestViewModel model, IManualPluginViewRegistry pluginRegistry,
        IChannelRuntimeCoordinator runtime, IResourceEditorService? resourceEditor = null,
        ManualPanelWorkspaceViewModel? panels = null, ISequenceDialogService? dialogs = null)
    {
        Model = model; Panels = panels; _registry = pluginRegistry; _runtime = runtime; _resourceEditor = resourceEditor; _dialogs = dialogs;
        InitializeComponent(); DataContext = model;
        if (Panels is not null) { Panels.LayoutChanged += LayoutChanged; Panels.PropertyChanged += PanelPropertyChanged; }
        Loaded += (_, _) => Reconcile();
        IsVisibleChanged += (_, _) => { if (IsVisible) Reconcile(); else DevicePicker.IsOpen = false; };
        SizeChanged += (_, _) => QueueLayout();
        RefreshStatus();
    }
    public void SetWorkspaceActive(bool active) { }
    public void SetWorkspaceName(string name) => _workspaceName = string.IsNullOrWhiteSpace(name) ? UiText.ManualTest : name;
    private void LayoutChanged(object? sender, EventArgs args)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => LayoutChanged(sender, args)); return; }
        Reconcile();
    }
    private void PanelPropertyChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(Panels.Status)) RefreshStatus(); }
    private void RefreshStatus() { PanelStatus.Text = Panels?.Status ?? ""; PanelStatus.Visibility = string.IsNullOrEmpty(PanelStatus.Text) ? Visibility.Collapsed : Visibility.Visible; }
    private void Reconcile()
    {
        if (_disposed || _reconciling || Panels is null) return;
        _reconciling = true;
        try
        {
            foreach (var id in _surfaces.Keys.Where(id => Panels.Groups.All(g => g.Id != id)).ToArray())
            { DisposeSurface(_surfaces[id]); _surfaces.Remove(id); }
            foreach (var (id, surface) in _surfaces)
            {
                var group = Panels.Groups.First(g => g.Id == id);
                foreach (var entry in surface.Panels.Where(p => !group.Panels.Contains(p.Reference) ||
                             !_registry.IsEnabled(p.Reference.FamilyId) || !Panels.Choices.Any(c => c.DeviceId == p.Reference.DeviceId && c.FamilyId == p.Reference.FamilyId)).ToArray())
                { surface.Workspace.CloseDocument(entry.Document); if (entry.View is IDisposable d) d.Dispose(); surface.Panels.Remove(entry); }
            }
            if (!IsVisible || !IsLoaded || Panels.ActiveGroup is not { } active) return;
            if (!_surfaces.TryGetValue(active.Id, out var target))
            {
                var workspace = new MdiWorkspace { View = WorkspaceView.Tabbed, MinHeight = 0 };
                workspace.SetResourceReference(BackgroundProperty, "ContentBrush"); workspace.SetResourceReference(ForegroundProperty, "TextBrush");
                target = new(workspace); _surfaces.Add(active.Id, target);
                var captured = target;
                workspace.DocumentClosed += (_, document) =>
                {
                    if (_reconciling || _disposed || captured.Panels.FirstOrDefault(p => p.Document == document) is not { } entry) return;
                    captured.Panels.Remove(entry); if (entry.View is IDisposable d) d.Dispose();
                    Panels.RemovePanel(active, entry.Reference);
                };
                workspace.DocumentActivated += (_, document) =>
                {
                    if (!_reconciling && !_applyingLayout && captured.Panels.FirstOrDefault(p => p.Document == document) is { } entry)
                        Panels.ActivatePanel(active, entry.Reference.DeviceId);
                };
            }
            if (!ReferenceEquals(WorkspaceHost.Content, target.Workspace)) WorkspaceHost.Content = target.Workspace;
            foreach (var panel in active.Panels)
            {
                if (target.Panels.Any(p => p.Reference == panel)) continue;
                var choice = Panels.Choices.FirstOrDefault(c => c.DeviceId == panel.DeviceId && c.FamilyId == panel.FamilyId);
                var provider = _registry.Providers.FirstOrDefault(p => p.Id == panel.FamilyId);
                if (choice is null || provider is null) continue;
                FrameworkElement? view = null;
                try
                {
                    view = provider.CreateView(new(Model.DocumentId, _workspaceName, _runtime, _resourceEditor, panel.DeviceId));
                    if (view is IDevicePanelSelection selection) selection.SelectDevice(panel.DeviceId);
                    else if (view.DataContext is IDevicePanelSelection contextSelection) contextSelection.SelectDevice(panel.DeviceId);
                    else throw new InvalidOperationException(UiText.Get("ManualInstanceBindingUnsupported"));
                }
                catch (Exception error)
                {
                    if (view is IDisposable failedView) failedView.Dispose();
                    view = new TextBlock { Text = UiText.Format("ManualPluginLoadFailed", error.Message), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16) };
                    view.SetResourceReference(TextBlock.ForegroundProperty, "ErrorBrush");
                }
                var scroll = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
                var document = target.Workspace.AddDocument($"{choice.Name} · {choice.DeviceId}", scroll);
                if (document.Child is Grid layout)
                {
                    foreach (var body in layout.Children.OfType<Border>().Where(b => Grid.GetRow(b) == 1)) body.Padding = new Thickness(0);
                    SetCaptionIcon(layout, NavigationIcons.ForFamily(provider));
                }
                target.Panels.Add(new(panel, document, view));
            }
            RefreshTitles(target);
            if (target.Panels.FirstOrDefault(p => p.Reference.DeviceId == active.ActiveDeviceId) is { } selected) target.Workspace.ActivateDocument(selected.Document);
            ViewToggleButton.Content = active.Layout == "tabs" ? UiText.ManualWindowView : UiText.ManualTabbedView;
            EmptyPluginState.Text = active.Panels.Count > target.Panels.Count ? UiText.Get("ManualUnavailablePanels") : UiText.ManualPluginEmptyHint;
            EmptyPluginState.Visibility = target.Panels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AddPluginButton.IsEnabled = Panels.Choices.Count > 0;
            QueueLayout();
        }
        finally { _reconciling = false; }
    }
    private void QueueLayout()
    {
        if (_layoutQueued || _disposed || !IsVisible) return;
        _layoutQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => { _layoutQueued = false; ApplyLayout(); }));
    }
    private void ApplyLayout()
    {
        _applyingLayout = true;
        try { ApplyLayoutCore(); }
        finally { _applyingLayout = false; }
    }
    private void ApplyLayoutCore()
    {
        if (_disposed || Panels?.ActiveGroup is not { } group || !_surfaces.TryGetValue(group.Id, out var surface)) return;
        var workspace = surface.Workspace;
        var view = group.Layout == "tabs" ? WorkspaceView.Tabbed : WorkspaceView.Windows;
        if (workspace.View != view) workspace.View = view;
        if (view == WorkspaceView.Tabbed) { RefreshTitles(surface); return; }
        if (group.Layout == "cascade") { workspace.Cascade(); return; }
        var count = surface.Panels.Count; if (count == 0) return;
        var columns = group.Layout == "tile" && count == 1 ? 1 : 2;
        var rows = (int)Math.Ceiling(count / (double)columns);
        var width = Math.Max(480, (workspace.ActualWidth - 24) / columns);
        var height = Math.Max(group.Layout == "columns" ? 480 : 300, (workspace.ActualHeight - 24) / rows);
        var previous = workspace.ActiveDocument;
        for (var i = 0; i < count; i++)
        {
            var document = surface.Panels[i].Document;
            workspace.RestoreDocument(document); workspace.MoveDocument(document, 6 + i % columns * width, 6 + i / columns * height);
            workspace.ResizeDocument(document, width - 8, height - 8);
        }
        if (previous is not null) workspace.ActivateDocument(previous);
    }
    private void ToggleView_Click(object sender, RoutedEventArgs args) => Panels?.SetLayout(Panels.ActiveGroup?.Layout == "tabs" ? "tile" : "tabs");
    private void Tile_Click(object sender, RoutedEventArgs args) => Panels?.SetLayout("tile");
    private void Cascade_Click(object sender, RoutedEventArgs args) => Panels?.SetLayout("cascade");
    private void Columns_Click(object sender, RoutedEventArgs args) => Panels?.SetLayout("columns");
    private void Quad_Click(object sender, RoutedEventArgs args) => Panels?.SetLayout("quad");
    private void GroupMenu_Click(object sender, RoutedEventArgs args)
    {
        if (Panels is null || _dialogs is null) return;
        var menu = new ContextMenu { PlacementTarget = GroupMenuButton, Placement = PlacementMode.Bottom };
        var add = new MenuItem { Header = UiText.Get("ManualNewGroup") }; add.Click += async (_, _) => await EditGroup(true);
        var rename = new MenuItem { Header = UiText.Get("ManualRenameGroup") }; rename.Click += async (_, _) => await EditGroup(false);
        var delete = new MenuItem { Header = UiText.Get("ManualDeleteGroup") };
        delete.Click += async (_, _) => { if (await _dialogs.ConfirmDeleteItemAsync(UiText.Get("ManualGroup"), Panels.ActiveGroup?.Name ?? "")) Panels.DeleteGroup(); };
        menu.Items.Add(add); menu.Items.Add(rename); menu.Items.Add(delete); menu.IsOpen = true;
    }
    private async Task EditGroup(bool create)
    {
        if (Panels is null || _dialogs is null) return;
        var name = await _dialogs.RequestNameAsync(UiText.Get(create ? "ManualNewGroup" : "ManualRenameGroup"), create ? UiText.Format("ManualGroupNumber", Panels.Groups.Count + 1) : Panels.ActiveGroup?.Name ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        try { if (create) Panels.AddGroup(name); else Panels.RenameGroup(name); }
        catch (Exception error) { ShowError(error); }
    }
    private void ShowError(Exception error) { PanelStatus.Text = error.Message; PanelStatus.Visibility = Visibility.Visible; }
    private void AddPlugin_Click(object sender, RoutedEventArgs args)
    {
        if (Panels is null) return;
        _pickerRows = Panels.Choices.Select(c => new PickerRow(c)).ToList();
        DeviceSearch.Text = ""; FilterPicker(); DevicePicker.IsOpen = true; DeviceSearch.Focus();
    }
    private void DeviceSearch_Changed(object sender, TextChangedEventArgs args) { if (DeviceChoices is not null) FilterPicker(); }
    private void FilterPicker()
    {
        var search = DeviceSearch.Text.Trim();
        DeviceChoices.ItemsSource = _pickerRows.Where(r => r.Caption.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Choice.Model.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        PickerEmpty.Visibility = DeviceChoices.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AddSelected_Click(object sender, RoutedEventArgs args)
    {
        try { Panels?.AddDevices(_pickerRows.Where(r => r.Picked).Select(r => r.Choice)); DevicePicker.IsOpen = false; }
        catch (Exception error) { ShowError(error); }
    }
    private void OpenedPanels_Click(object sender, RoutedEventArgs args)
    {
        if (Panels is null) return;
        var menu = new ContextMenu { PlacementTarget = OpenedPanelsButton, Placement = PlacementMode.Bottom };
        foreach (var group in Panels.Groups)
        {
            var header = new MenuItem { Header = group.Name };
            foreach (var panel in group.Panels)
            {
                var choice = Panels.Choices.FirstOrDefault(c => c.DeviceId == panel.DeviceId && c.FamilyId == panel.FamilyId);
                var item = new MenuItem { Header = choice?.Caption ?? panel.DeviceId, IsEnabled = choice is not null };
                item.Click += (_, _) => { Panels.ActiveGroup = group; if (_surfaces.TryGetValue(group.Id, out var surface) && surface.Panels.FirstOrDefault(p => p.Reference == panel) is { } entry) surface.Workspace.ActivateDocument(entry.Document); };
                header.Items.Add(item);
            }
            header.IsEnabled = group.Panels.Count > 0; menu.Items.Add(header);
        }
        menu.IsOpen = true;
    }
    private static void SetCaptionIcon(Grid layout, string icon)
    {
        if (!Enum.TryParse<MaterialDesignThemes.Wpf.PackIconKind>(icon, out var kind)) return;
        var caption = layout.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 0);
        if (caption?.Child is Grid captionGrid && captionGrid.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<MaterialDesignThemes.Wpf.PackIcon>().FirstOrDefault() is { } packIcon) packIcon.Kind = kind;
    }
    private void RefreshTitles(GroupSurface surface)
    {
        foreach (var entry in surface.Panels)
        {
            var choice = Panels?.Choices.FirstOrDefault(c => c.DeviceId == entry.Reference.DeviceId && c.FamilyId == entry.Reference.FamilyId);
            if (choice is null) continue;
            var title = $"{choice.Name} · {choice.DeviceId}";
            if (entry.Document.Child is Grid layout &&
                layout.Children.OfType<Border>().FirstOrDefault(b => Grid.GetRow(b) == 0)?.Child is Grid caption &&
                caption.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>()).FirstOrDefault() is { } heading) heading.Text = title;
            if (surface.Workspace.Content is Grid root)
                foreach (var tab in root.Children.OfType<NavigationTabs>().SelectMany(t => t.Items.OfType<NavigationTabItem>()).Where(t => t.Tag == entry.Document))
                { tab.Header = title; tab.ToolTip = title; }
        }
    }
    private static void DisposeSurface(GroupSurface surface)
    { foreach (var entry in surface.Panels) if (entry.View is IDisposable d) d.Dispose(); surface.Panels.Clear(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (Panels is not null) { Panels.LayoutChanged -= LayoutChanged; Panels.PropertyChanged -= PanelPropertyChanged; }
        DevicePicker.IsOpen = false; foreach (var surface in _surfaces.Values) DisposeSurface(surface); _surfaces.Clear(); WorkspaceHost.Content = null;
        Panels?.Dispose();
    }
}
