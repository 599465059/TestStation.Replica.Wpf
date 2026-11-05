using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.App.ViewModels;

public sealed partial class TransportResourceEditorViewModel : ObservableObject
{
    private readonly IResourceConfigurationService _configuration;
    private readonly ITransportDiscoveryService _discovery;
    private TransportResource _draft;
    private bool _loading;
    public IReadOnlyList<string> Types { get; }
    public ObservableCollection<TransportParameterField> Fields { get; } = [];
    public ObservableCollection<CanHardwareChannel> HardwareChannels { get; } = [];
    public ObservableCollection<string> SerialPorts { get; } = [];
    public IReadOnlyList<TransportBackendOption> CanBackends => _discovery.CanBackends;
    public IReadOnlyList<TransportBackendOption> VisaBackends => _discovery.VisaBackends;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _type = "tcp";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private CanHardwareChannel? _selectedHardware;
    [ObservableProperty] private string? _selectedPort;
    public bool IsSerial => Type == "serial";
    public bool IsCan => Type == "can";
    public string BackendNote => Type == "visa" ? string.Join("；", VisaBackends.Select(b => b.Note)) :
        Type == "can" ? string.Join("；", CanBackends.Where(b => !b.Available).Select(b => b.Name + "：" + b.Note)) : "";
    public TransportResourceEditorViewModel(IResourceConfigurationService configuration, ITransportDiscoveryService discovery,
        TransportResource? existing, IReadOnlyList<string>? allowedTypes)
    {
        _configuration = configuration; _discovery = discovery;
        Types = existing is null ? allowedTypes ?? ["tcp", "udp", "serial", "can", "visa"] : [TransportSettingsAccessor.NormalizeType(existing.Type)];
        _draft = existing is null ? configuration.CreateDraft(Types[0]) : Clone(existing);
        _loading = true; Name = _draft.Name; Enabled = _draft.Enabled; Type = Types[0]; _loading = false; LoadFields();
    }
    private static TransportResource Clone(TransportResource resource) => JsonSerializer.Deserialize<TransportResource>(JsonSerializer.Serialize(resource))!;
    partial void OnTypeChanged(string value)
    {
        if (_loading) return;
        var next = _configuration.CreateDraft(value); next.Id = _draft.Id; _draft = next;
        LoadFields();
    }
    private void LoadFields()
    {
        Fields.Clear();
        foreach (var (key, label) in TransportSettingsAccessor.Schema(Type))
        {
            var options = key switch
            {
                "mode" => new[] { "RS232", "RS422", "RS485" },
                "parity" => ["None", "Odd", "Even", "Mark", "Space"],
                "stop_bits" => ["1", "1.5", "2", "One", "Two", "OnePointFive"],
                "flow_control" => ["None", "RtsCts", "XOnXOff"],
                "terminator" => ["\\n", "\\r\\n", "\\r"],
                "fd_enabled" or "listen_only" or "half_duplex" => ["false", "true"],
                "can_backend_id" => CanBackends.Where(b => b.Available).Select(b => b.Id).ToArray(),
                _ => Array.Empty<string>()
            };
            Fields.Add(new(key, label, TransportSettingsAccessor.ReadValue(_draft, key), options));
        }
        foreach (var property in new[] { nameof(IsSerial), nameof(IsCan), nameof(BackendNote) }) OnPropertyChanged(property);
        HardwareChannels.Clear(); SelectedHardware = null; SerialPorts.Clear();
    }
    partial void OnSelectedHardwareChanged(CanHardwareChannel? value)
    { if (value is not null) Set("channel", value.ChannelId); }
    partial void OnSelectedPortChanged(string? value) { if (value is not null) Set("port", value); }
    private void Set(string key, string value) { var field = Fields.FirstOrDefault(f => f.Key == key); if (field is not null) field.Value = value; }
    [RelayCommand] private async Task ScanAsync()
    {
        IsScanning = true;
        try
        {
            Error = "";
            if (IsSerial) { SerialPorts.Clear(); foreach (var port in _discovery.SerialPorts()) SerialPorts.Add(port); }
            if (IsCan)
            {
                HardwareChannels.Clear();
                var backend = Fields.First(f => f.Key == "can_backend_id").Value;
                foreach (var channel in await _discovery.CanChannelsAsync(backend)) HardwareChannels.Add(channel);
            }
        }
        catch (Exception error) { Error = error.Message; }
        finally { IsScanning = false; }
    }
    public TransportResource BuildDraft()
    {
        var draft = Clone(_draft); draft.Name = Name.Trim(); draft.Enabled = Enabled;
        draft.LeaseMode = Type == "can" ? "shared_bus" : "exclusive";
        if (!TransportSettingsAccessor.TryApply(draft, Fields.ToDictionary(f => f.Key, f => f.Value), out var error)) throw new InvalidDataException(error);
        TransportSettingsAccessor.ValidateEndpoint(draft);
        return draft;
    }
}

public sealed partial class TransportParameterField(string key, string label, string value, IReadOnlyList<string> options) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public IReadOnlyList<string> Options { get; } = options;
    public bool HasOptions => Options.Count > 0;
    [ObservableProperty] private string _value = value;
}
