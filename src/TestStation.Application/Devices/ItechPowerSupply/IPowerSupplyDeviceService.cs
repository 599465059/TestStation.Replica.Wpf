using System.Text.Json;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices.ItechPowerSupply;

/// <param name="OutputEnabled">Last known output state; null until read or written after connecting.</param>
public sealed record PowerSupplyConnection(bool Connected = false, string Identity = "", string Error = "", bool? OutputEnabled = null);
public sealed record DeviceTraffic(DateTimeOffset Time, string DeviceId, string Direction, string Text);

public interface IPowerSupplyDeviceService : IAutomationChannelBackend, ISequenceChannelChangeSource
{
    IReadOnlyList<DeviceDefinition> Devices { get; }
    IReadOnlyList<TransportResource> Resources { get; }
    bool HasProject { get; }
    bool FamilyEnabled { get; set; }
    event EventHandler? StateChanged;
    event EventHandler<DeviceTraffic>? Traffic;
    event EventHandler<ChannelRuntimeValue>? ValueChanged;
    PowerSupplyConnection ConnectionFor(string deviceId);
    /// <summary>Turn the output off before any session is closed (manual disconnect, project switch,
    /// app exit, end of an automatic run). Default true; a failure is reported, never ignored.</summary>
    bool TurnOutputOffBeforeDisconnect { get; set; }
    PowerSupplyModel ModelFor(string deviceId);
    IReadOnlyList<PowerSupplyChannel> ChannelsFor(string deviceId);
    Task ConnectAsync(string deviceId, CancellationToken token = default);
    Task DisconnectDeviceAsync(string deviceId, CancellationToken token = default);
    DeviceDefinition AddDevice(string name);
    void UpdateDevice(string id, string name, string modelId, string resourceId);
    void RemoveDevice(string id);
    void SaveResource(TransportResource resource);
    Task SaveFamilyAsync(PowerSupplyFamily family, CancellationToken token = default);
}
