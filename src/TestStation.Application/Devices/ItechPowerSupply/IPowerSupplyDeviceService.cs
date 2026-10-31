using System.Text.Json;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices.ItechPowerSupply;

public sealed record PowerSupplyConnection(bool Connected = false, string Identity = "", string Error = "");
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
