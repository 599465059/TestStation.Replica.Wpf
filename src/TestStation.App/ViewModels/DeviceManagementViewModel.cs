using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Domain.Resources;

namespace TestStation.App.ViewModels;

public sealed partial class DeviceManagementViewModel : ObservableObject
{
    private readonly IResourceCatalogService _catalog;
    private readonly IResourceManager _resources;
    private readonly IResourceConfigurationService _configuration;
    private readonly IResourceEditorService _editor;
    private readonly IDeviceManagementService _devices;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly ITransportDiagnosticService _diagnostics;
    private readonly ProtocolDialogService _dialogs;
    private readonly DeviceNavigationService _navigation;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private CancellationTokenSource? _operation;
    public ObservableCollection<ResourceManagementRow> Resources { get; } = [];
    public ObservableCollection<DeviceManagementRow> Devices { get; } = [];
    public ObservableCollection<string> Traffic { get; } = [];
    [ObservableProperty] private ResourceManagementRow? _selectedResource;
    [ObservableProperty] private DeviceManagementRow? _selectedDevice;
    [ObservableProperty] private bool _showResources = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _payload = "*IDN?";
    [ObservableProperty] private bool _hex;
    [ObservableProperty] private bool _expectResponse = true;
    [ObservableProperty] private bool _appendTerminator = true;
    [ObservableProperty] private string _canId = "5A5";
    [ObservableProperty] private bool _extended;
    [ObservableProperty] private bool _fd;
    [ObservableProperty] private bool _brs;
    [ObservableProperty] private int _timeoutMs = 1500;
    public bool ShowDevices => !ShowResources;
    public bool CanManage => _catalog.ProjectRoot is not null && !IsBusy && !_runtime.IsAutomationActive;
    public bool DiagnosticOpen => _diagnostics.IsOpen;
    public bool CanDiagnostic => CanManage && !DiagnosticOpen;
    public bool DiagnosticByte => !DiagnosticCan;
    public bool DiagnosticCan => _diagnostics.IsCan;
    public string DiagnosticState => DiagnosticOpen ? UiText.Get("HwDiagnosticOpened") + " · " + _diagnostics.ResourceId : UiText.Get("HwDiagnosticClosed");
    public DeviceManagementViewModel(IResourceCatalogService catalog, IResourceManager resources, IResourceConfigurationService configuration,
        IResourceEditorService editor, IDeviceManagementService devices, IChannelRuntimeCoordinator runtime,
        ITransportDiagnosticService diagnostics, ProtocolDialogService dialogs, DeviceNavigationService navigation)
    {
        _catalog = catalog; _resources = resources; _configuration = configuration; _editor = editor; _devices = devices;
        _runtime = runtime; _diagnostics = diagnostics; _dialogs = dialogs; _navigation = navigation;
        catalog.Changed += Changed; resources.Changed += Changed; runtime.AvailabilityChanged += Changed;
        foreach (var family in devices.Families) family.Changed += Changed;
        diagnostics.Traffic += (_, message) => Post(() => { Traffic.Add($"{DateTime.Now:HH:mm:ss.fff} {message}"); while (Traffic.Count > 200) Traffic.RemoveAt(0); });
        Reload();
    }
    public void OpenProject(string root) => _catalog.OpenProject(root);
    private void Post(Action action) { if (_context is null) action(); else _context.Post(_ => action(), null); }
    private void Changed(object? sender, EventArgs e) => Post(Reload);
    partial void OnShowResourcesChanged(bool value) { OnPropertyChanged(nameof(ShowDevices)); OnPropertyChanged(nameof(HardwareTabIndex)); }
    /// <summary>In-page tab strip on the hardware configuration page: 0 = communication resources, 1 = device instances.</summary>
    public int HardwareTabIndex { get => ShowResources ? 0 : 1; set => ShowResources = value != 1; }
    partial void OnIsBusyChanged(bool value) => NotifyState();
    private void NotifyState() { foreach (var name in new[] { nameof(CanManage), nameof(DiagnosticOpen), nameof(CanToggleDiagnostic), nameof(DiagnosticCan), nameof(DiagnosticByte), nameof(CanDiagnostic), nameof(DiagnosticState) }) OnPropertyChanged(name); }
    private void Reload()
    {
        var resourceId = SelectedResource?.Definition.Id; var deviceId = SelectedDevice?.Definition.Id;
        Resources.Clear();
        foreach (var resource in _catalog.Catalog.Transports)
        {
            var s = TransportSettingsAccessor.Read(resource);
            var summary = resource.Type switch { "tcp" => $"{s.Host}:{s.TcpPort}", "udp" => $"{s.Host}:{s.TcpPort}", "serial" => $"{s.Port} · {s.SerialMode} · {s.BaudRate}", "can" => $"{s.CanBackendId} · {s.CanChannel} · {s.CanBitrate}", "visa" => s.VisaResource ?? "", _ => resource.Type };
            var bound = string.Join("、", _catalog.Catalog.Devices.Where(d => d.TransportId == resource.Id).Select(d => d.Name));
            Resources.Add(new(resource, summary, bound, _resources.IsLeased(resource.Id) ? UiText.Get("HwInUse") : UiText.Get("HwIdle")));
        }
        Devices.Clear();
        foreach (var device in _catalog.Catalog.Devices)
        {
            var family = _devices.Families.FirstOrDefault(f => f.Id == device.FamilyId);
            var state = family?.GetState(device.Id);
            var label = family is null || !family.Enabled ? UiText.Get("HwUnavailable") : state?.Error.Length > 0 ? UiText.Get("HwError") : state?.Connected == true ? UiText.Get("HwConnected") : string.IsNullOrEmpty(device.TransportId) ? UiText.Get("HwUnbound") : UiText.Get("HwDisconnected");
            Devices.Add(new(device, family?.Name ?? device.FamilyId, family?.Models.FirstOrDefault(m => m.Id == device.ModelId)?.Name ?? device.ModelId,
                _resources.Find(device.TransportId)?.Name ?? UiText.Get("HwUnbound"), label, state?.Error ?? "", _runtime.IsAutomationActive ? UiText.Get("HwAutoInUse") : UiText.Get("HwIdle")));
        }
        SelectedResource = Resources.FirstOrDefault(r => r.Definition.Id == resourceId) ?? Resources.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault(d => d.Definition.Id == deviceId) ?? Devices.FirstOrDefault();
        NotifyState();
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; _operation = new CancellationTokenSource();
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { Status = UiText.Get("HwCancelledTimeout"); }
        catch (Exception error) { Status = error.Message; }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; Reload(); }
    }
    [RelayCommand] private async Task NewResourceAsync() { if (!CanManage) return; await RunAsync(async _ => { var created = await _editor.EditAsync(); if (created is not null) { Reload(); SelectedResource = Resources.First(r => r.Definition.Id == created.Id); Status = UiText.Get("HwResourceSaved"); } }); }
    [RelayCommand] private async Task EditResourceAsync() { if (!CanManage || SelectedResource is null) return; var selected = SelectedResource.Definition; await RunAsync(async _ => { if (_resources.IsLeased(selected.Id)) throw new InvalidOperationException(UiText.Get("HwLeased")); await _editor.EditAsync(selected); }); }
    [RelayCommand] private async Task DeleteResourceAsync()
    {
        if (!CanManage || SelectedResource is null) return;
        var resource = SelectedResource.Definition;
        if (!await _dialogs.ConfirmMessageAsync(UiText.Delete, resource.Name)) return;
        await RunAsync(_ => _runtime.ExecuteManualAsync(() => { _configuration.Delete(resource.Id); return Task.CompletedTask; }));
    }
    [RelayCommand] private Task NewDeviceAsync() => EditDeviceCoreAsync(null);
    [RelayCommand] private Task EditDeviceAsync() => SelectedDevice is null ? Task.CompletedTask : EditDeviceCoreAsync(SelectedDevice.Definition);
    private async Task EditDeviceCoreAsync(DeviceDefinition? existing)
    {
        if (!CanManage) return;
        await RunAsync(async token =>
        {
            if (existing is not null && _devices.Resolve(existing.FamilyId).GetState(existing.Id).Connected) throw new InvalidOperationException(UiText.Get("HwDisconnectBeforeEdit"));
            var model = new DeviceInstanceEditorViewModel(_devices, _resources, _editor, existing);
            while (await _dialogs.ShowDeviceEditorAsync(model))
            {
                try
                {
                    model.Validate();
                    await _runtime.ExecuteManualAsync(() =>
                    {
                        var family = model.SelectedFamily!; DeviceDefinition? created = null;
                        try
                        {
                            var id = existing?.Id ?? (created = family.Add(model.Name.Trim())).Id;
                            family.Update(id, model.Name.Trim(), model.SelectedModel!.Id, model.SelectedResource?.Id ?? "");
                        }
                        catch { if (created is not null) family.Remove(created.Id); throw; }
                        return Task.CompletedTask;
                    }, token);
                    Status = UiText.Get("HwInstanceSaved"); break;
                }
                catch (Exception error) { model.Error = error.Message; }
            }
        });
    }
    [RelayCommand] private async Task DeleteDeviceAsync()
    {
        if (!CanManage || SelectedDevice is null) return;
        var device = SelectedDevice.Definition;
        if (!await _dialogs.ConfirmMessageAsync(UiText.Delete, UiText.Format("HwDeleteDevicePrompt", device.Name))) return;
        await RunAsync(token => _runtime.ExecuteManualAsync(() => { _devices.Resolve(device.FamilyId).Remove(device.Id); return Task.CompletedTask; }, token));
    }
    [RelayCommand] private async Task ConnectAsync()
    { if (!CanManage || SelectedDevice is null) return; var device = SelectedDevice.Definition; Status = UiText.Get("HwConnecting"); await RunAsync(token => _runtime.ExecuteManualAsync(() => _devices.Resolve(device.FamilyId).ConnectAsync(device.Id, token), token)); }
    [RelayCommand] private async Task DisconnectAsync()
    { if (!CanManage || SelectedDevice is null) return; var device = SelectedDevice.Definition; await RunAsync(token => _runtime.ExecuteManualAsync(() => _devices.Resolve(device.FamilyId).DisconnectAsync(device.Id, token), token)); }
    [RelayCommand] private void OpenPanel() { if (SelectedDevice is not null) _navigation.Open(SelectedDevice.Definition.FamilyId, SelectedDevice.Definition.Id); }
    [RelayCommand] private async Task OpenDiagnosticAsync()
    { if (!CanDiagnostic || SelectedResource is null) return; var id = SelectedResource.Definition.Id; await RunAsync(token => _diagnostics.OpenAsync(id, token)); }
    [RelayCommand] private async Task CloseDiagnosticAsync() { Cancel(); await _diagnostics.CloseAsync(); Reload(); }
    /// <summary>One button: opens diagnostics for the selected resource, or closes the open session.</summary>
    public bool CanToggleDiagnostic => DiagnosticOpen || CanDiagnostic;
    [RelayCommand] private Task ToggleDiagnosticAsync() => DiagnosticOpen ? CloseDiagnosticAsync() : OpenDiagnosticAsync();
    [RelayCommand] private void Cancel() => _operation?.Cancel();
    [RelayCommand] private void ClearTraffic() => Traffic.Clear();
    private byte[] Bytes()
    {
        if (Hex) return Convert.FromHexString(Payload.Replace(" ", "").Replace("-", ""));
        var resource = _resources.Find(_diagnostics.ResourceId);
        var suffix = AppendTerminator && resource is not null && resource.Settings.TryGetValue("terminator", out var value) ? value.GetString() ?? "" : "";
        return Encoding.ASCII.GetBytes(Payload + suffix);
    }
    [RelayCommand] private async Task SendAsync()
    {
        if (!CanManage || !DiagnosticOpen) return;
        await RunAsync(async token =>
        {
            if (TimeoutMs is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(TimeoutMs));
            if (DiagnosticCan)
            {
                var data = Convert.FromHexString(Payload.Replace(" ", "").Replace("-", ""));
                var frame = new CanFrame(ParseCanId(), data, IsExtended: Extended, IsFd: Fd, BitRateSwitch: Brs); frame.Validate();
                await _diagnostics.SendCanAsync(frame, token);
            }
            else
            {
                var response = await _diagnostics.ExchangeAsync(Bytes(), ExpectResponse, TimeSpan.FromMilliseconds(TimeoutMs), token);
                if (response.HasValue) Status = Hex ? Convert.ToHexString(response.Value.Span) : Encoding.ASCII.GetString(response.Value.Span);
            }
        });
    }
    private uint ParseCanId() => uint.Parse(CanId.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? CanId[2..] : CanId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    [RelayCommand] private async Task ReadCanAsync()
    {
        if (!CanManage || !DiagnosticOpen || !DiagnosticCan) return;
        await RunAsync(async token => { if (TimeoutMs is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(TimeoutMs)); var frame = await _diagnostics.ReadCanAsync(new(Id: ParseCanId(), IsExtended: Extended, IsFd: Fd), TimeSpan.FromMilliseconds(TimeoutMs), token); Status = $"{frame.Id:X} {Convert.ToHexString(frame.Data.Span)}"; });
    }
    public async Task LeaveAsync() { Cancel(); await _diagnostics.CloseAsync(); NotifyState(); }
}

public sealed record ResourceManagementRow(TransportResource Definition, string Summary, string BoundDevices, string Occupancy)
{
    /// <summary>Display name of the stored transport type id.</summary>
    public string TypeText => TestStation.Application.Resources.TransportSettingsAccessor.NormalizeType(Definition.Type) switch
    { "tcp" => "TCP", "udp" => "UDP", "serial" => UiText.Get("HwTypeSerial"), "can" => "CAN", "visa" => "VISA", var other => other.ToUpperInvariant() };
}
public sealed record DeviceManagementRow(DeviceDefinition Definition, string Family, string Model, string Resource, string State, string Error, string Occupancy);
