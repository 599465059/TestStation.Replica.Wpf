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
    public IReadOnlyList<IManualPluginViewProvider> Families => _registry.InstalledProviders;

    public DeviceTestingView(IManualPluginViewRegistry registry, IChannelRuntimeCoordinator runtime, IResourceEditorService? resourceEditor = null)
    {
        _registry = registry; _runtime = runtime; _resourceEditor = resourceEditor;
        InitializeComponent();
        _registry.Changed += RegistryChanged;
    }

    private void RegistryChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => RegistryChanged(sender, e)); return; }
        foreach (var pair in _views.Where(p => !_registry.IsEnabled(p.Key)).ToArray())
        {
            if (pair.Value.View is IDisposable view) view.Dispose();
            _views.Remove(pair.Key);
        }
        if (_familyId is not null) ShowFamily(_familyId);
    }

    public void ShowFamily(string familyId)
    {
        _familyId = familyId;
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
        if (_familyId is not null && _views.TryGetValue(_familyId, out var entry) && entry.View is IDevicePanelSelection panel) panel.SelectDevice(deviceId);
    }

    public void Dispose()
    {
        _registry.Changed -= RegistryChanged;
        foreach (var view in _views.Values.Select(v => v.View).OfType<IDisposable>()) view.Dispose();
        _views.Clear();
    }
}
