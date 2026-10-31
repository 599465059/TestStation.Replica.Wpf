using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.App.Services;

namespace TestStation.App.Devices.ItechPowerSupply.ViewModels;

public partial class ItechPowerSupplyAdminViewModel : ObservableObject, IDisposable
{
    private readonly IItechPowerSupplyProfileStore _store;
    private readonly IPowerSupplyDeviceService _devices;
    private readonly IChannelRuntimeCoordinator _runtime;
    private readonly ISequenceDialogService _dialogs;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private string _saved = "";
    public PowerSupplyFamily Draft { get; private set; } = new();
    public IReadOnlyList<PowerSupplyModel> Models => Draft.Models.ToArray();
    public IReadOnlyList<string> Targets => new[] { "家族公共定义" }.Concat(Draft.Models.Select(m => m.Name + " [" + m.Id + "]")).ToArray();
    public IReadOnlyList<PowerSupplyChannel> Channels => (TargetModel?.Channels ?? Draft.Channels).ToArray();
    public bool IsInherited => TargetModel is { Channels: null };
    public bool ChannelsEditable => CanEdit && !IsInherited;
    public bool CanEdit => !_runtime.IsAutomationActive;
    public bool IsDirty => JsonSerializer.Serialize(Draft) != _saved;
    public string[] Types => PowerSupplyValidation.Types;
    public string[] Bindings => PowerSupplyValidation.Bindings;
    public string[] Categories { get; } = ["型号与能力", "通道定义", "SCPI 指令", "轮询策略"];
    public HashSet<string> ExistingChannelIds { get; private set; } = [];
    public HashSet<string> ExistingModelIds { get; private set; } = [];
    [ObservableProperty] private int _categoryIndex;
    [ObservableProperty] private int _targetIndex;
    [ObservableProperty] private PowerSupplyModel? _selectedModel;
    [ObservableProperty] private PowerSupplyChannel? _selectedChannel;
    [ObservableProperty] private string _status = "";
    private PowerSupplyModel? TargetModel => TargetIndex > 0 && TargetIndex <= Draft.Models.Count ? Draft.Models[TargetIndex - 1] : null;
    public ItechPowerSupplyAdminViewModel(IItechPowerSupplyProfileStore store, IPowerSupplyDeviceService devices,
        IChannelRuntimeCoordinator runtime, ISequenceDialogService dialogs)
    { _store = store; _devices = devices; _runtime = runtime; _dialogs = dialogs; Reload(); _runtime.AvailabilityChanged += AvailabilityChanged; }
    private void AvailabilityChanged(object? sender, EventArgs args)
    { void Update() { OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(ChannelsEditable)); } if (_context is null) Update(); else _context.Post(_ => Update(), null); }
    public void Reload()
    {
        Draft = _store.Family; _saved = JsonSerializer.Serialize(Draft);
        ExistingChannelIds = Draft.Channels.Concat(Draft.Models.SelectMany(m => m.Channels ?? [])).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ExistingModelIds = Draft.Models.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        TargetIndex = 0; SelectedModel = Draft.Models.FirstOrDefault(); SelectedChannel = null; NotifyDraft();
    }
    public void NotifyDraft()
    { foreach (var name in new[] { nameof(Draft), nameof(Models), nameof(Targets), nameof(Channels), nameof(IsInherited), nameof(ChannelsEditable), nameof(IsDirty) }) OnPropertyChanged(name); }
    partial void OnTargetIndexChanged(int value) { SelectedChannel = null; NotifyDraft(); }
    [RelayCommand] private void AddModel()
    { if (!CanEdit) return; Draft.Models.Add(new() { Id = "model_" + Guid.NewGuid().ToString("N")[..6], Name = "新模拟型号" }); SelectedModel = Draft.Models.Last(); NotifyDraft(); }
    [RelayCommand] private void RemoveModel()
    {
        if (!CanEdit || SelectedModel is null) return;
        if (_devices.Devices.Any(d => d.ModelId == SelectedModel.Id)) { Status = "设备仍引用此型号，请先修改设备型号"; return; }
        Draft.Models.Remove(SelectedModel); TargetIndex = 0; SelectedModel = Draft.Models.FirstOrDefault(); NotifyDraft();
    }
    [RelayCommand] private void OverrideChannels()
    { if (!CanEdit || TargetModel is null) return; TargetModel.Channels = Draft.Channels.Select(c => c with { }).ToList(); NotifyDraft(); }
    [RelayCommand] private void InheritChannels()
    { if (!CanEdit || TargetModel is null) return; TargetModel.Channels = null; NotifyDraft(); }
    [RelayCommand] private void AddChannel()
    {
        if (!ChannelsEditable) return;
        (TargetModel?.Channels ?? Draft.Channels).Add(new("channel_" + Guid.NewGuid().ToString("N")[..6], "新通道", "number", "", true, false, "MEAS:VOLT?"));
        SelectedChannel = Channels.Last(); NotifyDraft();
    }
    [RelayCommand] private void RemoveChannel()
    {
        if (!ChannelsEditable || SelectedChannel is null) return;
        if (ExistingChannelIds.Contains(SelectedChannel.Id)) { SelectedChannel.Enabled = false; Status = "已有通道已停用，标识保留以保护序列引用"; }
        else (TargetModel?.Channels ?? Draft.Channels).Remove(SelectedChannel);
        SelectedChannel = null; NotifyDraft();
    }
    [RelayCommand] private void Validate()
    { try { PowerSupplyValidation.Validate(Draft); Status = "型号、通道、指令和轮询校验通过"; } catch (Exception e) { Status = e.Message; } }
    [RelayCommand] private async Task SaveAsync() { await CommitAsync(Draft); }
    private async Task<bool> CommitAsync(PowerSupplyFamily family)
    {
        if (!CanEdit) { Status = "自动测试期间不能修改配置"; return false; }
        try
        {
            PowerSupplyValidation.Validate(family);
            var connected = _devices.Devices.Where(d => _devices.ConnectionFor(d.Id).Connected).ToArray();
            if (connected.Length > 0)
            {
                if (!await _dialogs.ConfirmDeleteItemAsync("连接（保存配置需要断开所有电源）", string.Join("、", connected.Select(d => d.Name)))) return false;
                foreach (var device in connected) await _devices.DisconnectDeviceAsync(device.Id);
            }
            await _devices.SaveFamilyAsync(family); Reload(); Status = "配置已保存；设备保持断开，请重新连接"; return true;
        }
        catch (Exception e) { Status = e.Message; return false; }
    }
    [RelayCommand] private async Task DiscardAsync()
    { if (!IsDirty || await _dialogs.ConfirmDeleteItemAsync("未保存修改", "家族配置")) { Reload(); Status = "已放弃修改"; } }
    [RelayCommand] private async Task ResetAsync()
    { if (!CanEdit) return; if (await _dialogs.ConfirmDeleteItemAsync("自定义配置", "恢复公共 SCPI 默认配置")) await CommitAsync(PowerSupplyFamily.Default()); }
    public async Task<bool> PrepareToLeaveAsync()
    {
        if (!IsDirty) return true;
        var choice = await _dialogs.AskUnsavedSequenceAsync("电源家族配置");
        if (choice == SequenceUnsavedChoice.Cancel) return false;
        if (choice == SequenceUnsavedChoice.Save) return await CommitAsync(Draft);
        Reload(); return true;
    }
    public void Dispose() => _runtime.AvailabilityChanged -= AvailabilityChanged;
}
