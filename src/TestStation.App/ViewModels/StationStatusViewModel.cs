using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.App.Localization;
using TestStation.Application.Devices;
using TestStation.Application.Resources;

namespace TestStation.App.ViewModels;

/// <summary>
/// Station-wide summary for the shell status bar: how many configured device
/// instances are connected. Reads only the device management contracts; it does
/// not open sessions or poll devices.
/// </summary>
public sealed partial class StationStatusViewModel : ObservableObject, IDisposable
{
    private readonly IResourceCatalogService _catalog;
    private readonly IDeviceManagementService _devices;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    [ObservableProperty] private int _configuredDevices;
    [ObservableProperty] private int _connectedDevices;
    [ObservableProperty] private int _faultedDevices;
    [ObservableProperty] private int _liveDevices;

    public StationStatusViewModel(IResourceCatalogService catalog, IDeviceManagementService devices)
    {
        _catalog = catalog; _devices = devices;
        _catalog.Changed += Changed;
        foreach (var family in _devices.Families) family.Changed += Changed;
        Refresh();
    }

    public bool HasDevices => ConfiguredDevices > 0;
    public bool HasFaults => FaultedDevices > 0;
    /// <summary>At least one connected device reports its output as on (read from the device, never assumed).</summary>
    public bool HasLiveOutputs => LiveDevices > 0;
    public string LiveText => UiText.Format("StatusLiveOutputs", LiveDevices);
    public string DevicesText => UiText.Format("StatusDevicesOnline", ConnectedDevices, ConfiguredDevices);

    public void Refresh()
    {
        var configured = 0; var connected = 0; var faulted = 0; var live = 0;
        foreach (var device in _catalog.Catalog.Devices)
        {
            configured++;
            var family = _devices.Families.FirstOrDefault(f => f.Id.Equals(device.FamilyId, StringComparison.OrdinalIgnoreCase));
            if (family is null || !family.Enabled) continue;
            var state = family.GetState(device.Id);
            if (state.Error.Length > 0) faulted++;
            else if (state.Connected) connected++;
            if (state.Connected && state.OutputEnabled == true) live++;
        }
        ConfiguredDevices = configured; ConnectedDevices = connected; FaultedDevices = faulted; LiveDevices = live;
        OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(HasFaults)); OnPropertyChanged(nameof(DevicesText));
        OnPropertyChanged(nameof(HasLiveOutputs)); OnPropertyChanged(nameof(LiveText));
    }

    // Family and catalog events may come from device I/O threads.
    private void Changed(object? sender, EventArgs e)
    {
        if (_context is null || SynchronizationContext.Current == _context) Refresh();
        else _context.Post(_ => Refresh(), null);
    }

    public void Dispose()
    {
        _catalog.Changed -= Changed;
        foreach (var family in _devices.Families) family.Changed -= Changed;
    }
}
