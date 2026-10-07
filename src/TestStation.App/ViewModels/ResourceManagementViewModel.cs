using System.IO;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Resources;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;

namespace TestStation.App.ViewModels;

public partial class ResourceManagementViewModel : ObservableObject, TestStation.App.Services.IProjectSaveParticipant
{
    private readonly IResourceCatalogService _service;
    private readonly IResourceCatalogEditingService _editor;
    private readonly IDriverCatalog _drivers;
    public ObservableCollection<TransportResource> Transports { get; } = [];
    public ObservableCollection<DeviceDefinition> Devices { get; } = [];
    public ObservableCollection<ChannelDefinition> Channels { get; } = [];
    public ObservableCollection<TransportSettingEntry> TransportSettings { get; } = [];
    [ObservableProperty] private TransportResource? _selectedTransport;
    [ObservableProperty] private DeviceDefinition? _selectedDevice;
    [ObservableProperty] private ChannelDefinition? _selectedChannel;
    [ObservableProperty] private string _status = "请先打开或创建工程";
    public IReadOnlyList<DriverDescriptor> Drivers => _drivers.GetAll();
    public bool HasProject => _service.ProjectRoot is not null;

    public ResourceManagementViewModel(IResourceCatalogService service, IResourceCatalogEditingService editor, IDriverCatalog drivers)
    {
        _service = service; _editor = editor; _drivers = drivers; _service.Changed += (_, _) => Reload();
    }
    public void OpenProject(string root) { _service.OpenProject(root); }
    private void Reload()
    {
        var transportId = SelectedTransport?.Id; var deviceId = SelectedDevice?.Id; var channelId = SelectedChannel?.Id;
        Transports.Clear(); foreach (var item in _service.Catalog.Transports) Transports.Add(item);
        Devices.Clear(); foreach (var item in _service.Catalog.Devices) Devices.Add(item);
        SelectedTransport = Transports.FirstOrDefault(item => item.Id == transportId) ?? Transports.FirstOrDefault();
        SelectedDevice = Devices.FirstOrDefault(item => item.Id == deviceId) ?? Devices.FirstOrDefault();
        RefreshChannels(channelId);
        Status = _service.Errors.Count > 0 ? string.Join("；", _service.Errors) : $"通信资源 {Transports.Count} 个，设备 {Devices.Count} 个";
        OnPropertyChanged(nameof(HasProject));
    }
    partial void OnSelectedDeviceChanged(DeviceDefinition? value) { RefreshChannels(null); }
    partial void OnSelectedTransportChanged(TransportResource? value) => RefreshTransportSettings();
    private void RefreshTransportSettings()
    {
        TransportSettings.Clear();
        if (SelectedTransport is null) return;
        foreach (var (key, label) in TransportSettingsAccessor.Schema(SelectedTransport.Type))
            TransportSettings.Add(new TransportSettingEntry(key, label, TransportSettingsAccessor.ReadValue(SelectedTransport, key)));
    }
    private void RefreshChannels(string? preferredId)
    {
        Channels.Clear();
        if (SelectedDevice is not null) foreach (var channel in SelectedDevice.Channels) Channels.Add(channel);
        SelectedChannel = Channels.FirstOrDefault(item => item.Id == preferredId) ?? Channels.FirstOrDefault();
    }
    [RelayCommand] private void NewTransport()
    {
        if (!HasProject) return;
        var item = _editor.AddTransport(); Reload(); SelectedTransport = item; Status = "已添加通信资源，修改参数后请保存";
    }
    [RelayCommand] private void NewDevice()
    {
        if (!HasProject || !Transports.Any()) { Status = "请先创建通信资源"; return; }
        var transport = SelectedTransport ?? Transports[0];
        var item = _editor.AddDevice(transport.Id); Reload(); SelectedDevice = item; Status = "已添加设备，修改参数后请保存";
    }
    [RelayCommand] private void NewChannel()
    {
        if (SelectedDevice is null) { Status = "请先选择设备"; return; }
        var item = _editor.AddChannel(SelectedDevice.Id); RefreshChannels(null); SelectedChannel = item; Status = "已添加通道，修改参数后请保存";
    }
    [RelayCommand] private void DeleteSelected()
    {
        if (SelectedChannel is not null && SelectedDevice is not null) { if (_editor.RemoveChannel(SelectedDevice.Id, SelectedChannel.Id, out var channelError)) { RefreshChannels(null); Status = "已删除通道"; } else Status = channelError; return; }
        if (SelectedDevice is not null) { if (_editor.RemoveDevice(SelectedDevice.Id, out var deviceError)) { Reload(); Status = "已删除设备"; } else Status = deviceError; return; }
        if (SelectedTransport is not null)
        {
            if (_editor.RemoveTransport(SelectedTransport.Id, out var transportError)) { Reload(); Status = "已删除通信资源"; } else Status = transportError;
        }
    }
    [RelayCommand] private void Save()
    {
        try { _service.Save(_service.Catalog); Status = "资源配置已保存"; }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or InvalidOperationException) { Status = error.Message; }
    }
    public string SaveSection => "设备与通信资源";
    public TestStation.App.Services.ProjectSaveResult SaveProjectChanges()
    {
        if (!HasProject) return new(true);
        try
        {
            if (SelectedTransport is not null && !TransportSettingsAccessor.TryApply(SelectedTransport,
                TransportSettings.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase), out var error))
                return new(false, error);
            _service.Save(_service.Catalog); Status = "资源配置已保存"; return new(true);
        }
        catch (Exception error) { Status = error.Message; return new(false, Status); }
    }
    [RelayCommand] private void ApplyTransportSettings()
    {
        if (SelectedTransport is null) { Status = "请先选择通信资源"; return; }
        var values = TransportSettings.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        if (!TransportSettingsAccessor.TryApply(SelectedTransport, values, out var error)) { Status = error; return; }
        Status = "通信参数已更新，保存资源后生效";
    }
    [RelayCommand] private void Refresh() { if (HasProject) _service.OpenProject(_service.ProjectRoot!); }
}

public sealed partial class TransportSettingEntry : ObservableObject
{
    public TransportSettingEntry(string key, string label, string value) { Key = key; Label = label; _value = value; }
    public string Key { get; }
    public string Label { get; }
    [ObservableProperty] private string _value;
}
