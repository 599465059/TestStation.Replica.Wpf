using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.App.Services;
using TestStation.Domain.Resources;

namespace TestStation.App.Devices.ItechPowerSupply.ViewModels;

public partial class ItechPowerSupplyViewModel : ObservableObject, IDisposable
{
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly IPowerSupplyDeviceService _devices;
    private readonly IItechPowerSupplyProfileStore _profiles;
    private readonly IResourceManager _resources;
    private readonly ISequenceDialogService _dialogs;
    private readonly IResourceEditorService? _resourceEditor;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly System.Windows.Threading.DispatcherTimer _freshness = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _visible;
    private bool _refreshing;
    private DeviceDefinition[] _deviceItems = [];
    private PowerSupplyModel[] _modelItems = [];
    private TransportResource[] _resourceItems = [];
    public IReadOnlyList<DeviceDefinition> Devices => _deviceItems;
    public IReadOnlyList<PowerSupplyModel> Models => _modelItems;
    public IReadOnlyList<TransportResource> Resources => _resourceItems.Where(r => r.Enabled &&
        (r.Type == "tcp" && Model?.Tcp == true || r.Type == "serial" && Model?.Serial == true)).ToArray();
    public ObservableCollection<PowerSupplyField> Fields { get; } = [];
    public IReadOnlyList<PowerSupplyField> Setpoints => Fields.Where(f => f.Definition.Binding is "voltage" or "current").ToArray();
    public IReadOnlyList<PowerSupplyField> Measurements => Fields.Where(f => f.Definition.Binding is "voltage_measurement" or "current_measurement").ToArray();
    public IReadOnlyList<PowerSupplyField> Protection => Fields.Where(f => f.Definition.Binding == "protection").ToArray();
    public IReadOnlyList<PowerSupplyField> Extra => Fields.Where(f => f.Definition.Binding.Length == 0).ToArray();
    public PowerSupplyField? Output => Fields.FirstOrDefault(f => f.Definition.Binding == "output");
    public bool HasProtection => Protection.Count > 0;
    public bool HasExtra => Extra.Count > 0;
    public bool HasOutput => Output is not null;
    public PowerSupplyModel? Model => SelectedModel;
    public bool IsConnected => SelectedDevice is not null && _devices.ConnectionFor(SelectedDevice.Id).Connected;
    public bool IsAutomationActive => _runtime.IsAutomationActive;
    public bool CanConfigure => _devices.HasProject && !IsBusy && !IsAutomationActive && !IsConnected;
    public bool CanOperate => IsConnected && !IsBusy && !IsAutomationActive;
    public bool CanConnect => CanConfigure && SelectedDevice is not null && SelectedResource is not null;
    public bool CanDisconnect => IsConnected && !IsBusy && !IsAutomationActive;
    public bool CanManage => _devices.HasProject && !IsBusy && !IsAutomationActive;
    public string Protocol => "SCPI · itech.public-scpi";
    public string ConnectionLabel => IsAutomationActive ? "自动测试占用 · 手动访问暂停" : IsConnected ? "已连接" : "未连接";
    public string Identity => SelectedDevice is null ? "" : _devices.ConnectionFor(SelectedDevice.Id).Identity;
    public string ResourceSummary
    {
        get
        {
            if (SelectedResource is null) return "请选择通信资源";
            var s = TransportSettingsAccessor.Read(SelectedResource);
            return SelectedResource.Type == "tcp" ? $"TCP  {s.Host}:{s.TcpPort}" : $"串口  {s.Port}  {s.BaudRate} baud  {s.DataBits}/{s.Parity}/{s.StopBits}";
        }
    }
    public ObservableCollection<string> Traffic { get; } = [];
    [ObservableProperty] private DeviceDefinition? _selectedDevice;
    [ObservableProperty] private PowerSupplyModel? _selectedModel;
    [ObservableProperty] private TransportResource? _selectedResource;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoRefresh = true;
    partial void OnAutoRefreshChanged(bool value) => UpdatePolling();
    public ItechPowerSupplyViewModel(IChannelRuntimeCoordinator runtime, IPowerSupplyDeviceService devices,
        IItechPowerSupplyProfileStore profiles, IResourceManager resources, ISequenceDialogService dialogs, IResourceEditorService? resourceEditor = null)
    {
        _runtime = runtime; _devices = devices; _profiles = profiles; _resources = resources; _dialogs = dialogs; _resourceEditor = resourceEditor;
        _devices.Changed += DevicesChanged; _devices.StateChanged += DevicesStateChanged;
        _devices.Traffic += TrafficReceived; _devices.ValueChanged += DeviceValueChanged; _runtime.AvailabilityChanged += AvailabilityChanged;
        _freshness.Tick += (_, _) => { foreach (var field in Fields) field.RefreshAge(!IsConnected || IsAutomationActive); };
        Reload();
    }
    private void Post(Action action) { if (_context is null) action(); else _context.Post(_ => action(), null); }
    private void DevicesChanged(object? sender, EventArgs e) => Post(Reload);
    private void DevicesStateChanged(object? sender, EventArgs e) => Post(() =>
    {
        NotifyState(); UpdatePolling();
        if (SelectedDevice is not null && _devices.ConnectionFor(SelectedDevice.Id).Error is { Length: > 0 } error) Status = error;
    });
    private void AvailabilityChanged(object? sender, EventArgs e) => Post(() => { NotifyState(); foreach (var f in Fields) f.RefreshAge(IsAutomationActive || !IsConnected); });
    private void DeviceValueChanged(object? sender, ChannelRuntimeValue value) => Post(() =>
    {
        if (!_visible) return;
        var field = Fields.FirstOrDefault(f => f.Definition.Id.Equals(value.ChannelId, StringComparison.OrdinalIgnoreCase));
        field?.Accept(value);
    });
    private void TrafficReceived(object? sender, DeviceTraffic traffic) => Post(() =>
    { if (traffic.DeviceId != SelectedDevice?.Id) return; Traffic.Add($"{traffic.Time.ToLocalTime():HH:mm:ss.fff} {traffic.Direction}  {traffic.Text}"); while (Traffic.Count > 120) Traffic.RemoveAt(0); });
    private void Reload()
    {
        var id = SelectedDevice?.Id; _refreshing = true;
        _deviceItems = _devices.Devices.ToArray();
        _modelItems = _profiles.Family.Models.Where(m => m.Enabled).ToArray();
        _resourceItems = _devices.Resources.ToArray();
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == id) ?? Devices.FirstOrDefault();
        _refreshing = false; OnPropertyChanged(nameof(Devices)); LoadSelection();
    }
    partial void OnSelectedDeviceChanged(DeviceDefinition? value) { if (!_refreshing) LoadSelection(); }
    private void LoadSelection()
    {
        StopSubscriptions(); _refreshing = true;
        SelectedModel = Models.FirstOrDefault(m => m.Id == SelectedDevice?.ModelId);
        SelectedResource = Resources.FirstOrDefault(r => r.Id == SelectedDevice?.TransportId);
        Fields.Clear();
        if (SelectedDevice is not null)
            foreach (var channel in _devices.ChannelsFor(SelectedDevice.Id)) Fields.Add(new(channel, SelectedModel!));
        _refreshing = false;
        foreach (var name in new[] { nameof(Models), nameof(Resources), nameof(Model), nameof(Setpoints), nameof(Measurements), nameof(Protection), nameof(Extra), nameof(Output), nameof(HasExtra), nameof(HasOutput), nameof(HasProtection), nameof(ResourceSummary) }) OnPropertyChanged(name);
        NotifyState(); UpdatePolling();
    }
    partial void OnSelectedModelChanged(PowerSupplyModel? value) { if (!_refreshing) { OnPropertyChanged(nameof(Resources)); if (SelectedResource is null || !Resources.Any(r => r.Id == SelectedResource.Id)) SelectedResource = null; } }
    partial void OnSelectedResourceChanged(TransportResource? value) => OnPropertyChanged(nameof(ResourceSummary));
    partial void OnIsBusyChanged(bool value) => NotifyState();
    private void NotifyState()
    { foreach (var name in new[] { nameof(IsConnected), nameof(IsAutomationActive), nameof(CanConfigure), nameof(CanOperate), nameof(CanConnect), nameof(CanDisconnect), nameof(CanManage), nameof(ConnectionLabel), nameof(Identity) }) OnPropertyChanged(name); }
    public void StartPolling() { _visible = true; _freshness.Start(); UpdatePolling(); }
    public void StopPolling() { _visible = false; _freshness.Stop(); StopSubscriptions(); }
    private void StopSubscriptions() { foreach (var subscription in _subscriptions) subscription.Dispose(); _subscriptions.Clear(); }
    private void UpdatePolling()
    {
        StopSubscriptions(); if (!AutoRefresh || !_visible || !IsConnected || !_devices.FamilyEnabled) return;
        foreach (var field in Fields.Where(f => f.Definition.Poll && f.Definition.CanRead))
            _subscriptions.Add(_runtime.Subscribe(field.Definition.Id, TimeSpan.FromMilliseconds(field.Definition.IntervalMs), value => Post(() => field.Accept(value))));
    }
    private async Task RunAsync(Func<Task> action)
    { IsBusy = true; try { await action(); } catch (Exception error) { Status = error.Message; } finally { IsBusy = false; } }
    [RelayCommand] private async Task SaveBindingAsync()
    {
        if (!CanConfigure || SelectedDevice is null || SelectedModel is null) return;
        await RunAsync(() => { _devices.UpdateDevice(SelectedDevice.Id, SelectedDevice.Name, SelectedModel.Id, SelectedResource?.Id ?? ""); Status = SelectedResource is null ? "设备配置已保存，尚未绑定通信资源" : "设备型号和资源绑定已保存"; return Task.CompletedTask; });
    }
    [RelayCommand] private async Task ConnectAsync()
    {
        if (!CanConnect || SelectedDevice is null || SelectedModel is null || SelectedResource is null) return;
        var id = SelectedDevice.Id; var name = SelectedDevice.Name; var model = SelectedModel.Id; var resource = SelectedResource.Id;
        await RunAsync(() => _runtime.ExecuteManualAsync(async () => { _devices.UpdateDevice(id, name, model, resource); await _devices.ConnectAsync(id); Status = "已连接"; }));
    }
    [RelayCommand] private async Task DisconnectAsync()
    { if (!CanDisconnect || SelectedDevice is null) return; await RunAsync(() => _runtime.ExecuteManualAsync(async () => { await _devices.DisconnectDeviceAsync(SelectedDevice.Id); Status = "已断开"; })); }
    [RelayCommand] private async Task AddDeviceAsync()
    {
        if (!CanManage) return;
        var name = await _dialogs.RequestNameAsync("新增电源实例", "新电源"); if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => { var created = _devices.AddDevice(name); Reload(); SelectedDevice = Devices.First(d => d.Id == created.Id); return Task.CompletedTask; });
    }
    [RelayCommand] private async Task RenameDeviceAsync()
    {
        if (!CanConfigure || SelectedDevice is null) return;
        var name = await _dialogs.RequestNameAsync("重命名电源", SelectedDevice.Name); if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(() => { _devices.UpdateDevice(SelectedDevice.Id, name, SelectedDevice.ModelId, SelectedDevice.TransportId); return Task.CompletedTask; });
    }
    [RelayCommand] private async Task RemoveDeviceAsync()
    {
        if (!CanConfigure || SelectedDevice is null) return;
        if (!await _dialogs.ConfirmDeleteItemAsync("设备实例（已有序列引用将失效）", SelectedDevice.Name)) return;
        await RunAsync(() => { _devices.RemoveDevice(SelectedDevice.Id); return Task.CompletedTask; });
    }
    [RelayCommand] private async Task ApplyAsync()
    {
        if (!CanOperate) return;
        await RunAsync(async () => { foreach (var field in Setpoints.Where(f => f.Definition.CanWrite)) await _runtime.WriteAsync(field.Definition.Id, field.ReadInput(), "manual"); Status = "输出设定已发送"; });
    }
    [RelayCommand] private async Task ApplyOutputAsync()
    { if (!CanOperate || Output is null) return; await RunAsync(async () => { await _runtime.WriteAsync(Output.Definition.Id, Output.ReadInput(), "manual"); Status = "输出开关已发送"; }); }
    [RelayCommand] private async Task ReadAsync()
    {
        if (!CanOperate) return;
        await RunAsync(async () => { foreach (var field in Fields.Where(f => f.Definition.CanRead)) field.Accept(await _runtime.ReadOnceAsync(field.Definition.Id, "manual")); Status = "读取完成"; });
    }
    [RelayCommand] private async Task ReadFieldAsync(PowerSupplyField? field)
    { if (!CanOperate || field is null || !field.Definition.CanRead) return; await RunAsync(async () => field.Accept(await _runtime.ReadOnceAsync(field.Definition.Id, "manual"))); }
    [RelayCommand] private async Task WriteFieldAsync(PowerSupplyField? field)
    { if (!CanOperate || field is null || !field.Definition.CanWrite) return; await RunAsync(async () => { await _runtime.WriteAsync(field.Definition.Id, field.ReadInput(), "manual"); Status = "通道写入完成"; }); }
    [RelayCommand] private Task EditResourceAsync() => EditResourceCoreAsync(false);
    [RelayCommand] private Task NewResourceAsync() => EditResourceCoreAsync(true);
    private async Task EditResourceCoreAsync(bool create)
    {
        if (!CanConfigure || _resourceEditor is null || (!create && SelectedResource is null)) return;
        await RunAsync(async () =>
        {
            var resource = await _resourceEditor.EditAsync(create ? null : SelectedResource, ["tcp", "serial"]);
            if (resource is null) return;
            Reload(); SelectedResource = Resources.FirstOrDefault(r => r.Id == resource.Id);
        });
    }

    public void Dispose()
    { StopPolling(); _devices.Changed -= DevicesChanged; _devices.StateChanged -= DevicesStateChanged; _devices.Traffic -= TrafficReceived; _devices.ValueChanged -= DeviceValueChanged; _runtime.AvailabilityChanged -= AvailabilityChanged; }
}

public partial class PowerSupplyField : ObservableObject
{
    public PowerSupplyChannel Definition { get; }
    public string Name => Definition.DisplayName;
    public string Unit => Definition.Unit;
    public bool IsBoolean => Definition.DataType == "boolean";
    public bool IsText => !IsBoolean;
    public bool CanRead => Definition.CanRead;
    public bool CanWrite => Definition.CanWrite;
    public string Range { get; }
    [ObservableProperty] private string _input = "0";
    [ObservableProperty] private bool _boolInput;
    [ObservableProperty] private string _display = "—";
    [ObservableProperty] private string _quality = "未读取";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _qualityColor = "#888888";
    private DateTimeOffset? _lastValid;
    public PowerSupplyField(PowerSupplyChannel definition, PowerSupplyModel model)
    {
        Definition = definition;
        var max = definition.Binding == "voltage" ? Math.Min(definition.Maximum ?? model.MaxVoltage, model.MaxVoltage) : definition.Binding == "current" ? Math.Min(definition.Maximum ?? model.MaxCurrent, model.MaxCurrent) : definition.Maximum;
        Range = definition.DataType is "number" or "integer" ? $"{definition.Minimum?.ToString(CultureInfo.InvariantCulture) ?? "−∞"} … {max?.ToString(CultureInfo.InvariantCulture) ?? "+∞"} {Unit}" : definition.DataType;
        if (definition.DataType == "string") Input = "";
    }
    public JsonElement ReadInput() => Definition.DataType switch
    {
        "boolean" => JsonSerializer.SerializeToElement(BoolInput),
        "string" => JsonSerializer.SerializeToElement(Input),
        "integer" => JsonSerializer.SerializeToElement(long.Parse(Input, NumberStyles.Integer, CultureInfo.InvariantCulture)),
        _ => JsonSerializer.SerializeToElement(double.Parse(Input, NumberStyles.Float, CultureInfo.InvariantCulture))
    };
    public void Accept(ChannelRuntimeValue value)
    {
        if (value.Error.Length > 0) { Error = value.Error; Quality = "错误"; QualityColor = "#D94B4B"; return; }
        Display = value.Value.ValueKind == JsonValueKind.Number ? value.Value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) : value.Value.ToString();
        _lastValid = value.Timestamp; Error = ""; Quality = "有效"; QualityColor = "#26985B";
        if (!Definition.Poll && Definition.CanWrite) { if (IsBoolean) BoolInput = value.Value.GetBoolean(); else Input = Display; }
    }
    public void RefreshAge(bool suspended)
    {
        if (Error.Length > 0 || !_lastValid.HasValue) return;
        var stale = suspended || Definition.Poll && DateTimeOffset.Now - _lastValid > TimeSpan.FromMilliseconds(Math.Max(1500, Definition.IntervalMs * 3));
        Quality = stale ? "过期" : "有效"; QualityColor = stale ? "#C18A26" : "#26985B";
    }
}
