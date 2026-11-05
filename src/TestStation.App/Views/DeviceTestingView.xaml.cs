using System.Windows;
using System.Windows.Controls;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;

namespace TestStation.App.Views;

/// <summary>Hosts the same family panel used in manual workspaces, without an extra tab surface.</summary>
public partial class DeviceTestingView : UserControl, IDisposable
{
    public async Task<bool> PrepareToLeaveAsync()
    {
        foreach (var view in _views.Values.Select(v => v.View).OfType<TestStation.App.Devices.ItechPowerSupply.Views.ItechPowerSupplyFamilyView>())
            if (!await view.PrepareToLeaveAsync()) return false;
        return true;
    }
    private readonly IManualPluginViewRegistry _registry;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly IResourceEditorService? _resourceEditor;
    private readonly Dictionary<string, (FrameworkElement View, ScrollViewer Host)> _views = new(StringComparer.OrdinalIgnoreCase);
    private string? _familyId;
    private string? _deviceId;
    private string? _deviceFamilyOfSelection;
    private bool _syncingList;
    private readonly TestStation.Application.Resources.IResourceCatalogService? _catalog;
    private readonly TestStation.Application.Devices.IDeviceManagementService? _devices;
    public IReadOnlyList<IManualPluginViewProvider> Families => _registry.InstalledProviders;
    /// <summary>Family shown last; the shell reopens it when the user returns to the device page.</summary>
    public string? CurrentFamilyId => _familyId;

    /// <summary>Kind: family (header row) or device (instance row). State: online / live / fault / idle.</summary>
    private sealed record FamilyEntry(string Id, string Name, string Icon, string Kind = "family", string FamilyId = "", string State = "idle", string Tooltip = "");

    public DeviceTestingView(IManualPluginViewRegistry registry, IChannelRuntimeCoordinator runtime, IResourceEditorService? resourceEditor = null,
        TestStation.Application.Resources.IResourceCatalogService? catalog = null, TestStation.Application.Devices.IDeviceManagementService? devices = null)
    {
        _registry = registry; _runtime = runtime; _resourceEditor = resourceEditor; _catalog = catalog; _devices = devices;
        InitializeComponent();
        _registry.Changed += RegistryChanged;
        if (_catalog is not null) _catalog.Changed += DevicesChanged;
        if (_devices is not null) foreach (var family in _devices.Families) family.Changed += DevicesChanged;
        RefreshFamilyList();
    }

    // Device state events can arrive from device I/O threads.
    private void DevicesChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RefreshFamilyList); return; }
        RefreshFamilyList();
    }

    private IEnumerable<FamilyEntry> BuildEntries()
    {
        var managed = _devices?.Families.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var family in Families)
        {
            yield return new FamilyEntry(family.Id, family.Name, NavigationIcons.ForFamily(family), Tooltip: family.Name);
            if (_catalog is null) continue;
            foreach (var device in _catalog.Catalog.Devices.Where(d => d.FamilyId.Equals(family.Id, StringComparison.OrdinalIgnoreCase)))
            {
                var state = "idle"; var text = UiText.HwDisconnected;
                if (managed is not null && managed.TryGetValue(family.Id, out var managedFamily))
                {
                    var current = managedFamily.GetState(device.Id);
                    (state, text) = current.Error.Length > 0 ? ("fault", current.Error)
                        : current.Connected && current.OutputEnabled == true ? ("live", UiText.Get("StatusLiveOne"))
                        : current.Connected ? ("online", UiText.HwConnected) : ("idle", UiText.HwDisconnected);
                }
                yield return new FamilyEntry(device.Id, device.Name, "", "device", family.Id, state, $"{device.Name}：{text}");
            }
        }
    }

    private FamilyEntry? FindEntry() =>
        FamilyList.Items.OfType<FamilyEntry>().FirstOrDefault(e => e.Kind == "device" && _deviceId is not null &&
            e.Id.Equals(_deviceId, StringComparison.OrdinalIgnoreCase) && e.FamilyId.Equals(_familyId, StringComparison.OrdinalIgnoreCase))
        ?? FamilyList.Items.OfType<FamilyEntry>().FirstOrDefault(e => e.Kind == "family" && e.Id.Equals(_familyId, StringComparison.OrdinalIgnoreCase));

    private void RefreshFamilyList()
    {
        _syncingList = true;
        try
        {
            FamilyList.ItemsSource = BuildEntries().ToArray();
            FamilyList.SelectedItem = FindEntry();
            NoFamiliesState.Visibility = FamilyList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _syncingList = false; }
    }

    private async void FamilyList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList || FamilyList.SelectedItem is not FamilyEntry entry) return;
        var familyId = entry.Kind == "device" ? entry.FamilyId : entry.Id;
        var familyChanges = !familyId.Equals(_familyId, StringComparison.OrdinalIgnoreCase);
        // Same unsaved-input guard as leaving the page from the shell navigation.
        if (familyChanges && !await PrepareToLeaveAsync())
        {
            RefreshFamilyList();
            return;
        }
        if (familyChanges) ShowFamily(familyId);
        if (entry.Kind == "device") SelectDevice(entry.Id);
    }

    private void RegistryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => RegistryChanged(sender, e)); return; }
        foreach (var pair in _views.Where(p => !_registry.IsEnabled(p.Key)).ToArray())
        {
            if (pair.Value.View is IDisposable view) view.Dispose();
            _views.Remove(pair.Key);
        }
        RefreshFamilyList();
        if (_familyId is not null) ShowFamily(_familyId);
    }

    /// <summary>Shows the last family, else the first installed one, else the empty state.</summary>
    public void ShowDefault()
    {
        var id = _familyId ?? Families.FirstOrDefault()?.Id;
        if (id is not null) { ShowFamily(id); return; }
        FamilyContent.Content = null;
        UnavailableState.Text = UiText.NoDeviceFamilies;
        UnavailableState.Visibility = Visibility.Visible;
    }

    public void ShowFamily(string familyId)
    {
        _familyId = familyId;
        if (!familyId.Equals(_deviceFamilyOfSelection, StringComparison.OrdinalIgnoreCase)) _deviceId = null;
        _syncingList = true;
        try { FamilyList.SelectedItem = FindEntry(); }
        finally { _syncingList = false; }
        FamilyContent.Content = null;
        UnavailableState.Visibility = Visibility.Collapsed;
        var provider = _registry.Providers.FirstOrDefault(p => p.Id.Equals(familyId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            UnavailableState.Text = UiText.Get("DeviceFamilyUnavailable");
            UnavailableState.Visibility = Visibility.Visible;
            return;
        }
        try
        {
            if (!_views.TryGetValue(familyId, out var entry))
            {
                var view = provider.CreateView(new("device-testing", provider.Name, _runtime, _resourceEditor));
                entry = (view, new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto });
                _views.Add(familyId, entry);
            }
            FamilyContent.Content = entry.Host;
        }
        catch (Exception error)
        {
            UnavailableState.Text = UiText.Format("ManualPluginLoadFailed", error.Message);
            UnavailableState.Visibility = Visibility.Visible;
        }
    }

    public void SelectDevice(string deviceId)
    {
        _deviceId = deviceId; _deviceFamilyOfSelection = _familyId;
        if (_familyId is not null && _views.TryGetValue(_familyId, out var entry) && entry.View is IDevicePanelSelection panel) panel.SelectDevice(deviceId);
        _syncingList = true;
        try { FamilyList.SelectedItem = FindEntry(); }
        finally { _syncingList = false; }
    }

    public void Dispose()
    {
        _registry.Changed -= RegistryChanged;
        if (_catalog is not null) _catalog.Changed -= DevicesChanged;
        if (_devices is not null) foreach (var family in _devices.Families) family.Changed -= DevicesChanged;
        foreach (var view in _views.Values.Select(v => v.View).OfType<IDisposable>()) view.Dispose();
        _views.Clear();
    }
}
