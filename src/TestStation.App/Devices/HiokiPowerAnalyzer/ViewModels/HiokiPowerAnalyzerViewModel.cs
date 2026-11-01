using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.App.Devices.HiokiPowerAnalyzer.ViewModels;

public partial class HiokiPowerAnalyzerViewModel : ObservableObject, IDisposable
{
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly IHiokiPowerAnalyzerDeviceService _devices;
    private readonly IResourceManager _resources;
    private readonly ISequenceDialogService _dialogs;
    private readonly IResourceEditorService? _resourceEditor;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly System.Windows.Threading.DispatcherTimer _freshness = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _visible;
    private bool _refreshing;
    private DeviceDefinition[] _deviceItems = [];
    private TransportResource[] _resourceItems = [];

    public IReadOnlyList<DeviceDefinition> Devices => _deviceItems;
    public IReadOnlyList<TransportResource> Resources => _resourceItems.Where(r => r.Enabled && r.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase)).ToArray();
    public HiokiPowerAnalyzerModel Model => HiokiPowerAnalyzerCatalog.Model;
    public ObservableCollection<HiokiPowerAnalyzerField> Fields { get; } = [];
    public bool HasFields => Fields.Count > 0;
    public bool IsConnected => SelectedDevice is not null && _devices.ConnectionFor(SelectedDevice.Id).Connected;
    public bool IsAutomationActive => _runtime.IsAutomationActive;
    public bool CanManage => _devices.HasProject && !IsBusy && !IsAutomationActive;
    public bool CanConfigure => CanManage && !IsConnected;
    public bool CanOperate => IsConnected && !IsBusy && !IsAutomationActive;
    public bool CanConnect => CanConfigure && SelectedDevice is not null && SelectedResource is not null;
    public bool CanDisconnect => IsConnected && !IsBusy && !IsAutomationActive;
    public string ConnectionLabel => IsAutomationActive ? "自动测试占用 · 手动访问暂停" : IsConnected ? "已连接" : "未连接";
    public string Identity => SelectedDevice is null ? "" : _devices.ConnectionFor(SelectedDevice.Id).Identity;
    public string ResourceSummary => SelectedResource is null ? "请选择 TCP 资源" : TransportSettingsAccessor.Read(SelectedResource) is var s ? $"TCP  {s.Host}:{s.TcpPort}" : "";

    [ObservableProperty] private DeviceDefinition? _selectedDevice;
    [ObservableProperty] private TransportResource? _selectedResource;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _autoRefresh = true;
    partial void OnAutoRefreshChanged(bool value) => UpdatePolling();
    public HiokiPowerAnalyzerViewModel(IChannelRuntimeCoordinator runtime, IHiokiPowerAnalyzerDeviceService devices,
        IResourceManager resources, ISequenceDialogService dialogs, IResourceEditorService? resourceEditor = null)
    {
        _runtime = runtime; _devices = devices; _resources = resources; _dialogs = dialogs; _resourceEditor = resourceEditor;
        _devices.Changed += DevicesChanged; _devices.StateChanged += StateChanged; _devices.ValueChanged += ValueChanged;
        _runtime.AvailabilityChanged += RuntimeChanged;
        _freshness.Tick += (_, _) => { foreach (var field in Fields) field.RefreshAge(IsAutomationActive || !IsConnected); };
        Reload();
    }

    private void Post(Action action) { if (_context is null) action(); else _context.Post(_ => action(), null); }
    private void DevicesChanged(object? sender, EventArgs e) => Post(Reload);
    private void StateChanged(object? sender, EventArgs e) => Post(() => { NotifyState(); UpdatePolling(); var error = SelectedDevice is null ? "" : _devices.ConnectionFor(SelectedDevice.Id).Error; if (!string.IsNullOrWhiteSpace(error)) Status = error; });
    private void RuntimeChanged(object? sender, EventArgs e) => Post(() => { NotifyState(); foreach (var field in Fields) field.RefreshAge(IsAutomationActive || !IsConnected); });
    private void ValueChanged(object? sender, ChannelRuntimeValue value) => Post(() => Fields.FirstOrDefault(f => f.Definition.Id.Equals(value.ChannelId, StringComparison.OrdinalIgnoreCase))?.Accept(value));

    private void Reload()
    {
        var selectedId = SelectedDevice?.Id; _refreshing = true;
        _deviceItems = _devices.Devices.ToArray(); _resourceItems = _devices.Resources.ToArray();
        SelectedDevice = Devices.FirstOrDefault(d => d.Id == selectedId) ?? Devices.FirstOrDefault();
        _refreshing = false; OnPropertyChanged(nameof(Devices)); OnPropertyChanged(nameof(Resources)); LoadSelection();
    }

    partial void OnSelectedDeviceChanged(DeviceDefinition? value) { if (!_refreshing) LoadSelection(); }
    partial void OnSelectedResourceChanged(TransportResource? value) => OnPropertyChanged(nameof(ResourceSummary));
    private void LoadSelection()
    {
        StopSubscriptions(); _refreshing = true; SelectedResource = Resources.FirstOrDefault(r => r.Id == SelectedDevice?.TransportId); Fields.Clear();
        if (SelectedDevice is not null) foreach (var channel in HiokiPowerAnalyzerCatalog.Channels) Fields.Add(new HiokiPowerAnalyzerField(SelectedDevice.Id + "." + channel.Id, channel));
        _refreshing = false; OnPropertyChanged(nameof(ResourceSummary)); OnPropertyChanged(nameof(HasFields)); NotifyState(); UpdatePolling();
    }

    private void NotifyState() { foreach (var name in new[] { nameof(IsConnected), nameof(IsAutomationActive), nameof(CanManage), nameof(CanConfigure), nameof(CanOperate), nameof(CanConnect), nameof(CanDisconnect), nameof(ConnectionLabel), nameof(Identity) }) OnPropertyChanged(name); }
    public void StartPolling() { _visible = true; _freshness.Start(); UpdatePolling(); }
    public void StopPolling() { _visible = false; _freshness.Stop(); StopSubscriptions(); }
    private void StopSubscriptions() { foreach (var subscription in _subscriptions) subscription.Dispose(); _subscriptions.Clear(); }
    private void UpdatePolling()
    {
        StopSubscriptions(); if (!AutoRefresh || !_visible || !IsConnected || IsAutomationActive) return;
        foreach (var channel in Fields) _subscriptions.Add(_runtime.Subscribe(channel.DefinitionId, TimeSpan.FromMilliseconds(500), value => Post(() => channel.Accept(value))));
    }
    private async Task RunAsync(Func<Task> action) { IsBusy = true; try { await action(); } catch (Exception error) { Status = error.Message; } finally { IsBusy = false; NotifyState(); } }

    [RelayCommand] private async Task AddDeviceAsync()
    { if (!CanManage) return; var name = await _dialogs.RequestNameAsync("新增功率分析仪", "新 PW6001"); if (string.IsNullOrWhiteSpace(name)) return; await RunAsync(() => { var created = _devices.AddDevice(name); Reload(); SelectedDevice = Devices.FirstOrDefault(d => d.Id == created.Id); return Task.CompletedTask; }); }
    [RelayCommand] private async Task RenameDeviceAsync()
    { if (!CanConfigure || SelectedDevice is null) return; var name = await _dialogs.RequestNameAsync("重命名功率分析仪", SelectedDevice.Name); if (string.IsNullOrWhiteSpace(name)) return; await RunAsync(() => { _devices.UpdateDevice(SelectedDevice.Id, name, "PW6001", SelectedDevice.TransportId); return Task.CompletedTask; }); }
    [RelayCommand] private async Task RemoveDeviceAsync()
    { if (!CanConfigure || SelectedDevice is null) return; if (!await _dialogs.ConfirmDeleteItemAsync("设备实例", SelectedDevice.Name)) return; await RunAsync(() => { _devices.RemoveDevice(SelectedDevice.Id); Reload(); return Task.CompletedTask; }); }
    [RelayCommand] private async Task SaveBindingAsync()
    { if (!CanConfigure || SelectedDevice is null) return; await RunAsync(() => { _devices.UpdateDevice(SelectedDevice.Id, SelectedDevice.Name, "PW6001", SelectedResource?.Id ?? ""); Status = SelectedResource is null ? "已保存，尚未绑定资源" : "设备绑定已保存"; return Task.CompletedTask; }); }
    [RelayCommand] private async Task ConnectAsync()
    { if (!CanConnect || SelectedDevice is null || SelectedResource is null) return; await RunAsync(() => _runtime.ExecuteManualAsync(async () => { _devices.UpdateDevice(SelectedDevice.Id, SelectedDevice.Name, "PW6001", SelectedResource.Id); await _devices.ConnectAsync(SelectedDevice.Id); Status = "已连接"; })); }
    [RelayCommand] private async Task DisconnectAsync()
    { if (!CanDisconnect || SelectedDevice is null) return; await RunAsync(() => _runtime.ExecuteManualAsync(async () => { await _devices.DisconnectDeviceAsync(SelectedDevice.Id); Status = "已断开"; })); }
    [RelayCommand] private async Task ReadOnceAsync()
    { if (!CanOperate) return; await RunAsync(async () => { foreach (var field in Fields) field.Accept(await _runtime.ReadOnceAsync(field.DefinitionId, "manual")); Status = "读取完成"; }); }

    [RelayCommand] private Task EditResourceAsync() => EditResourceCoreAsync(false);
    [RelayCommand] private Task NewResourceAsync() => EditResourceCoreAsync(true);
    private async Task EditResourceCoreAsync(bool create)
    {
        if (!CanConfigure || _resourceEditor is null || (!create && SelectedResource is null)) return;
        await RunAsync(async () =>
        {
            var resource = await _resourceEditor.EditAsync(create ? null : SelectedResource, ["tcp"]);
            if (resource is null) return;
            Reload(); SelectedResource = Resources.FirstOrDefault(r => r.Id == resource.Id);
        });
    }

    public void Dispose() { StopPolling(); _devices.Changed -= DevicesChanged; _devices.StateChanged -= StateChanged; _devices.ValueChanged -= ValueChanged; _runtime.AvailabilityChanged -= RuntimeChanged; }
}

public partial class HiokiPowerAnalyzerField : ObservableObject
{
    public string DefinitionId { get; }
    public HiokiPowerAnalyzerChannel Definition { get; }
    public string Name => Definition.DisplayName;
    public string Unit => Definition.Unit;
    [ObservableProperty] private string _display = "—";
    [ObservableProperty] private string _quality = "未读取";
    [ObservableProperty] private string _qualityColor = "#888888";
    [ObservableProperty] private string _error = "";
    private DateTimeOffset? _lastValid;
    public HiokiPowerAnalyzerField(string definitionId, HiokiPowerAnalyzerChannel definition) { DefinitionId = definitionId; Definition = definition; }
    public void Accept(ChannelRuntimeValue value)
    { if (value.Error.Length > 0) { Error = value.Error; Quality = "错误"; QualityColor = "#D94B4B"; return; } Display = value.Value.ValueKind == JsonValueKind.Number ? value.Value.GetDouble().ToString("0.###", CultureInfo.InvariantCulture) : value.Value.ToString(); Error = ""; Quality = "有效"; QualityColor = "#26985B"; _lastValid = value.Timestamp; }
    public void RefreshAge(bool suspended) { if (Error.Length > 0 || !_lastValid.HasValue) return; var stale = suspended || DateTimeOffset.Now - _lastValid > TimeSpan.FromSeconds(2); Quality = stale ? "过期" : "有效"; QualityColor = stale ? "#C18A26" : "#26985B"; }
}
