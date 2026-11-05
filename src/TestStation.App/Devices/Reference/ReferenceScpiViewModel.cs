using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;
using TestStation.Domain.Devices;

namespace TestStation.App.Devices.Reference;

public sealed partial class ReferenceScpiViewModel : ObservableObject, IDevicePanelSelection, IDisposable
{
    private readonly IInstrumentDeviceService _service;
    private readonly IManagedDeviceFamily _family;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly IResourceManager _resources;
    private readonly ISequenceDialogService _dialogs;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly string _familyId;
    private readonly string? _pinnedDeviceId;
    public bool IsFamilyPanel => _pinnedDeviceId is null;
    public bool CanSelectInstance => _pinnedDeviceId is null && CanSelectDevice;
    private bool _visible;
    private bool _disposed;
    public ObservableCollection<DeviceDefinition> Devices { get; } = [];
    public ObservableCollection<InstrumentField> Fields { get; } = [];
    public IReadOnlyList<ManagedDeviceModel> Models => _family.Models;
    public IReadOnlyList<TransportResource> Resources => _resources.GetResources()
        .Where(r => r.Enabled && (SelectedModel is null || SelectedModel.TransportTypes.Contains(NormalizeResourceType(r.Type), StringComparer.OrdinalIgnoreCase))).ToArray();
    public string ProtocolText => SelectedModel is null ? UiText.RefSelectModel :
        $"{DefinitionFor(SelectedModel.Id)?.Protocol} · {string.Join(" / ", SelectedModel.TransportTypes)}";
    public string ModelStatus => SelectedModel is null ? "" : DefinitionFor(SelectedModel.Id)?.Validation ?? "";
    public bool ModelAvailable => SelectedModel is not null && DefinitionFor(SelectedModel.Id)?.RuntimeAvailable == true;
    [ObservableProperty] private DeviceDefinition? _selectedDevice;
    [ObservableProperty] private ManagedDeviceModel? _selectedModel;
    [ObservableProperty] private TransportResource? _selectedResource;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _autoRefresh = true;
    public bool IsConnected => SelectedDevice is not null && _service.StateFor(SelectedDevice.Id).Connected;
    public bool IsBusy => _runtime.IsAutomationActive;
    public bool CanSelectDevice => !IsBusy;
    public bool CanEdit => !IsBusy && !IsConnected;
    public bool CanOperate => IsConnected && !IsBusy && _family.Enabled;
    public bool CanConnect => CanEdit && _family.Enabled && ModelAvailable && SelectedDevice is not null && SelectedResource is not null;
    public string ConnectionText => IsBusy ? UiText.RefAutomationBusy : IsConnected ? UiText.RefConnected :
        $"{UiText.RefDisconnected} {_service.StateFor(SelectedDevice?.Id ?? "").Error}".Trim();

    public ReferenceScpiViewModel(string familyId, IInstrumentDeviceService service, IChannelRuntimeCoordinator runtime, IResourceManager resources, ISequenceDialogService dialogs, string? pinnedDeviceId = null)
    {
        _pinnedDeviceId = pinnedDeviceId;
        _familyId = familyId; _service = service; _family = service.Families.First(f => f.Id.Equals(familyId, StringComparison.OrdinalIgnoreCase));
        _runtime = runtime; _resources = resources; _dialogs = dialogs;
        _service.Changed += Changed; _service.StateChanged += StateChanged; _runtime.AvailabilityChanged += RuntimeChanged; _resources.Changed += ResourcesChanged;
        Reload();
    }
    public void SelectDevice(string deviceId) => OnUi(() => SelectedDevice = Devices.FirstOrDefault(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)));
    private void OnUi(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }), DispatcherPriority.DataBind);
    }
    private void Changed(object? sender, EventArgs args) => OnUi(Reload);
    private void ResourcesChanged(object? sender, EventArgs args) => OnUi(() => OnPropertyChanged(nameof(Resources)));
    private void StateChanged(object? sender, EventArgs args) => OnUi(() =>
    {
        if (!IsConnected) foreach (var field in Fields) field.MarkStale();
        NotifyAvailability();
        UpdatePolling();
    });
    private void RuntimeChanged(object? sender, EventArgs args) => OnUi(() => { NotifyAvailability(); UpdatePolling(); });
    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(IsConnected), nameof(IsBusy), nameof(ConnectionText), nameof(CanSelectDevice), nameof(CanSelectInstance), nameof(CanEdit), nameof(CanOperate), nameof(CanConnect), nameof(ModelAvailable), nameof(ModelStatus) })
            OnPropertyChanged(name);
    }
    private void Reload()
    {
        var id = _pinnedDeviceId ?? SelectedDevice?.Id;
        Devices.Clear();
        foreach (var device in _service.Devices.Where(d => d.FamilyId.Equals(_familyId, StringComparison.OrdinalIgnoreCase))) Devices.Add(device);
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == id) ?? (_pinnedDeviceId is null ? Devices.FirstOrDefault() : null);
        SelectedModel = Models.FirstOrDefault(m => m.Id.Equals(SelectedDevice?.ModelId, StringComparison.OrdinalIgnoreCase));
        SelectedResource = Resources.FirstOrDefault(r => r.Id == SelectedDevice?.TransportId);
        RefreshFields();
        OnPropertyChanged(nameof(Resources)); OnPropertyChanged(nameof(Models));
        NotifyAvailability();
    }
    partial void OnSelectedDeviceChanged(DeviceDefinition? value)
    {
        SelectedModel = Models.FirstOrDefault(m => m.Id.Equals(value?.ModelId, StringComparison.OrdinalIgnoreCase));
        SelectedResource = Resources.FirstOrDefault(r => r.Id == value?.TransportId);
        RefreshFields(); NotifyAvailability(); OnPropertyChanged(nameof(Resources)); OnPropertyChanged(nameof(ProtocolText));
    }
    partial void OnSelectedModelChanged(ManagedDeviceModel? value)
    {
        OnPropertyChanged(nameof(Resources)); OnPropertyChanged(nameof(ProtocolText));
        RefreshFields(); NotifyAvailability();
    }
    partial void OnSelectedResourceChanged(TransportResource? value) => NotifyAvailability();
    partial void OnAutoRefreshChanged(bool value) => UpdatePolling();
    private void RefreshFields()
    {
        StopPollingCore();
        var previous = Fields.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        Fields.Clear();
        var definition = SelectedModel is null ? null : DefinitionFor(SelectedModel.Id);
        if (SelectedDevice is not null && definition is not null)
            foreach (var channel in definition.Channels)
            {
                var id = SelectedDevice.Id + "." + channel.Id;
                Fields.Add(previous.TryGetValue(id, out var existing) && existing.Definition == channel ? existing : new InstrumentField(id, channel));
            }
        UpdatePolling();
    }
    private InstrumentModel? DefinitionFor(string modelId) => _service.Definitions.FirstOrDefault(f => f.Id.Equals(_familyId, StringComparison.OrdinalIgnoreCase))?.Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase));
    private static string NormalizeResourceType(string type) => type.ToLowerInvariant() switch { "modbus_tcp" => "tcp", "modbus_rtu" => "serial", "pcan" => "can", _ => type.ToLowerInvariant() };
    public void StartPolling() { if (_disposed) return; _visible = true; UpdatePolling(); }
    public void StopPolling() { _visible = false; StopPollingCore(); }
    private void StopPollingCore() { foreach (var subscription in _subscriptions) subscription.Dispose(); _subscriptions.Clear(); }
    private void UpdatePolling()
    {
        StopPollingCore();
        if (_disposed || !_visible || !AutoRefresh || !CanOperate) return;
        foreach (var field in Fields.Where(f => f.Definition.CanRead && f.Definition.IsMeasurement))
            _subscriptions.Add(_runtime.Subscribe(field.Id, TimeSpan.FromMilliseconds(500), value =>
                OnUi(() => { if (_visible && AutoRefresh && CanOperate && Fields.Contains(field)) field.Accept(value); })));
    }
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) { Status = error.Message; }
        finally { NotifyAvailability(); UpdatePolling(); }
    }
    [RelayCommand] private async Task NewDeviceAsync()
    {
        if (_pinnedDeviceId is not null || IsBusy || !_family.Enabled) return;
        var name = await _dialogs.RequestNameAsync(UiText.RefNewDevice, _family.Name);
        if (!string.IsNullOrWhiteSpace(name)) await Run(() => { var created = _family.Add(name); Reload(); SelectDevice(created.Id); return Task.CompletedTask; });
    }
    [RelayCommand] private async Task DeleteDeviceAsync()
    {
        if (!CanSelectInstance || !CanEdit || SelectedDevice is null || !await _dialogs.ConfirmDeleteItemAsync(UiText.RefDeviceInstance, SelectedDevice.Name)) return;
        await Run(() => { _family.Remove(SelectedDevice.Id); Reload(); return Task.CompletedTask; });
    }
    [RelayCommand] private async Task SaveBindingAsync()
    {
        if (!CanEdit || SelectedDevice is null || SelectedModel is null) return;
        await Run(() => { _family.Update(SelectedDevice.Id, SelectedDevice.Name, SelectedModel.Id, SelectedResource?.Id ?? ""); Status = UiText.RefBindingSaved; return Task.CompletedTask; });
    }
    [RelayCommand] private async Task ConnectAsync()
    {
        if (!CanConnect || SelectedDevice is null || SelectedModel is null || SelectedResource is null) return;
        var device = SelectedDevice; var model = SelectedModel; var resource = SelectedResource;
        await Run(async () =>
        {
            await _runtime.ExecuteManualAsync(async () =>
            {
                _family.Update(device.Id, device.Name, model.Id, resource.Id);
                await _family.ConnectAsync(device.Id);
            });
            Status = UiText.RefConnected;
        });
    }
    [RelayCommand] private async Task DisconnectAsync()
    {
        if (!CanOperate || SelectedDevice is null) return;
        var id = SelectedDevice.Id;
        await Run(async () => { await _runtime.ExecuteManualAsync(() => _family.DisconnectAsync(id)); Status = UiText.RefDisconnected; });
    }
    [RelayCommand] private async Task ReadOnceAsync()
    {
        if (!CanOperate) return;
        var fields = Fields.Where(f => f.Definition.CanRead).ToArray();
        await Run(async () => { foreach (var field in fields) await ReadFieldCoreAsync(field); Status = UiText.RefReadComplete; });
    }
    [RelayCommand] private async Task ReadFieldAsync(InstrumentField? field)
    { if (CanOperate && field?.Definition.CanRead == true) await Run(() => ReadFieldCoreAsync(field)); }
    private async Task ReadFieldCoreAsync(InstrumentField field)
    {
        try { field.Accept(await _runtime.ReadOnceAsync(field.Id, "manual")); }
        catch (Exception error) { field.MarkError(error.Message); throw; }
    }
    [RelayCommand] private async Task WriteFieldAsync(InstrumentField? field)
    {
        if (!CanOperate || field?.Definition.CanWrite != true) return;
        await Run(async () =>
        {
            var value = field.ParseInput();
            await _runtime.WriteAsync(field.Id, value, "manual");
            Status = UiText.RefWriteComplete;
            if (field.Definition.CanRead) await ReadFieldCoreAsync(field);
        });
    }
    public void Dispose()
    {
        if (_disposed) return;
        StopPolling(); _disposed = true;
        _service.Changed -= Changed; _service.StateChanged -= StateChanged; _runtime.AvailabilityChanged -= RuntimeChanged; _resources.Changed -= ResourcesChanged;
    }
}

public sealed partial class InstrumentField : ObservableObject
{
    public string Id { get; }
    public InstrumentChannel Definition { get; }
    public string Name => Definition.Name;
    public string Unit => Definition.Unit;
    public string DataType => Definition.DataType;
    public string DataTypeDisplay => UiDisplay.Option(DataType == "string" ? "text" : DataType);
    public bool CanRead => Definition.CanRead;
    public bool CanWrite => Definition.CanWrite;
    public bool CanSetValue => CanWrite && !Definition.IsAction;
    public string WriteCaption => Definition.IsAction ? UiText.RefExecute : UiText.RefWrite;
    public DateTimeOffset? LastValidTimestamp { get; private set; }
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _display = "—";
    [ObservableProperty] private string _quality = UiText.RefNotRead;
    public InstrumentField(string id, InstrumentChannel definition) { Id = id; Definition = definition; }
    public JsonElement ParseInput()
    {
        if (Definition.IsAction) return JsonSerializer.SerializeToElement(true);
        return DataType switch
        {
            "string" or "text" => JsonSerializer.SerializeToElement(Input),
            "boolean" when bool.TryParse(Input, out var value) => JsonSerializer.SerializeToElement(value),
            "boolean" when Input.Trim() is "1" or "0" => JsonSerializer.SerializeToElement(Input.Trim() == "1"),
            "integer" when long.TryParse(Input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => JsonSerializer.SerializeToElement(value),
            "number" when double.TryParse(Input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) => JsonSerializer.SerializeToElement(value),
            _ => throw new FormatException(UiText.RefInvalidInput + " " + DataType)
        };
    }
    public void Accept(ChannelRuntimeValue value)
    {
        if (value.Error.Length > 0) { MarkError(value.Error); return; }
        Display = value.Value.ValueKind == JsonValueKind.Number ? value.Value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) : value.Value.ToString();
        LastValidTimestamp = value.Timestamp;
        Quality = UiText.RefValid;
    }
    public void MarkError(string error) => Quality = UiText.RefError + error;
    public void MarkStale() { if (LastValidTimestamp.HasValue) Quality = UiText.RefStale; }
}
