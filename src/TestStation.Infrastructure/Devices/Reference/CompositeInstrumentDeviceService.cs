using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Drivers;
using TestStation.Application.Devices;
using TestStation.Domain.Devices;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Devices.Reference;

/// <summary>Aggregates protocol-specific family services behind one application contract.</summary>
public sealed class CompositeInstrumentDeviceService : IInstrumentDeviceService, IAsyncDisposable
{
    private readonly IReadOnlyList<IInstrumentDeviceService> _services;
    public CompositeInstrumentDeviceService(ReferenceScpiDeviceService scpi, ReferenceProtocolDeviceService protocols) { _services = [scpi, protocols]; foreach (var service in _services) { service.Changed += ForwardChanged; service.StateChanged += ForwardState; service.Traffic += ForwardTraffic; } }
    public IReadOnlyList<InstrumentFamily> Definitions => _services.SelectMany(s => s.Definitions).ToArray();
    public IReadOnlyList<IManagedDeviceFamily> Families => _services.SelectMany(s => s.Families).ToArray();
    public IReadOnlyList<DeviceDefinition> Devices => _services.SelectMany(s => s.Devices).ToArray();
    public IReadOnlyList<string> ChannelIds => _services.SelectMany(s => s.ChannelIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool IsAutomationActive { get => _services.Any(s => s.IsAutomationActive); set { foreach (var s in _services) s.IsAutomationActive = value; } }
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    public event EventHandler<InstrumentTraffic>? Traffic;
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => _services.SelectMany(s => s.GetChannels()).ToArray();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
    public bool OwnsChannel(string id) => _services.Any(s => s.OwnsChannel(id));
    private IInstrumentDeviceService ResolveChannel(string id) => _services.FirstOrDefault(s => s.OwnsChannel(id)) ?? throw new InvalidOperationException($"设备通道不可用：{id}");
    private IInstrumentDeviceService ResolveDevice(string id) => _services.FirstOrDefault(s => s.Devices.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) ?? throw new InvalidOperationException($"设备实例不存在：{id}");
    public InstrumentModel ModelFor(string id) => ResolveDevice(id).ModelFor(id);
    public ManagedDeviceState StateFor(string id) => ResolveDevice(id).StateFor(id);
    public Task SetFamilyEnabledAsync(string familyId, bool enabled, CancellationToken token = default) => ResolveFamily(familyId).SetFamilyEnabledAsync(familyId, enabled, token);
    public Task<JsonElement> ReadAsync(string id, CancellationToken token = default) => ResolveChannel(id).ReadAsync(id, token);
    public Task WriteAsync(string id, JsonElement value, CancellationToken token = default) => ResolveChannel(id).WriteAsync(id, value, token);
    public Task DisconnectAsync(CancellationToken token = default) => Task.WhenAll(_services.Select(s => s.DisconnectAsync(token)));
    public Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken token = default) =>
        AutomationBackendLifecycle.BeginAsync(_services, channels, token);
    public Task EndAutomationAsync(CancellationToken token = default) => AutomationBackendLifecycle.EndAsync(_services);
    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services) { service.Changed -= ForwardChanged; service.StateChanged -= ForwardState; service.Traffic -= ForwardTraffic; }
        await AutomationBackendLifecycle.DisposeAsync(_services).ConfigureAwait(false);
    }
    private IInstrumentDeviceService ResolveFamily(string id) => _services.FirstOrDefault(s => s.Families.Any(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) ?? throw new InvalidOperationException("设备家族未接入");
    private void ForwardChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);
    private void ForwardState(object? sender, EventArgs e) => StateChanged?.Invoke(this, e);
    private void ForwardTraffic(object? sender, InstrumentTraffic e) => Traffic?.Invoke(this, e);
}
