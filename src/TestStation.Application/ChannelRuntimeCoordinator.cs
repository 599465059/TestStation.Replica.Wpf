using System.Text.Json;

namespace TestStation.Application;

/// <summary>A value observed from a device channel.</summary>
public sealed record ChannelRuntimeValue(string ChannelId, JsonElement Value, DateTimeOffset Timestamp);

/// <summary>
/// Shared boundary between manual views and automatic execution. A real
/// DeviceHost can implement the same contract later; the current app uses the
/// simulator implementation.
/// </summary>
public interface IChannelRuntimeCoordinator
{
    bool IsAutomationActive { get; }
    event EventHandler? AvailabilityChanged;
    IDisposable Subscribe(string channelId, TimeSpan interval, Action<ChannelRuntimeValue> onValue);
    Task<ChannelRuntimeValue> ReadOnceAsync(string channelId, string owner, CancellationToken cancellationToken = default);
    Task WriteAsync(string channelId, JsonElement value, string owner, CancellationToken cancellationToken = default);
    IDisposable AcquireAutomation(string owner, IEnumerable<string> channels);
}
