namespace TestStation.Application.Hardware;

/// <summary>Discovery does not open a controller, change its bitrate or transmit.</summary>
public sealed record CanHardwareChannel(string BackendId, string ChannelId, string Name,
    string DeviceId, int ControllerIndex, bool SupportsFd, bool Available);

public interface ICanHardwareDiscovery
{
    Task<IReadOnlyList<CanHardwareChannel>> EnumerateAsync(string backendId,
        CancellationToken cancellationToken = default);
}
