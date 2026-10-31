using System.Text.Json;

namespace TestStation.Application;

/// <summary>A value observed from a device channel.</summary>
public sealed record ChannelRuntimeValue(string ChannelId, JsonElement Value, DateTimeOffset Timestamp, string Error = "");

/// <summary>
/// Shared boundary between manual views and automatic execution. A real
/// DeviceHost can implement the same contract later. Device I/O stays behind
/// this boundary even when the execution engine lives in another process.
/// </summary>
public interface IChannelRuntimeCoordinator
{
    bool IsAutomationActive { get; }
    event EventHandler? AvailabilityChanged;
    IDisposable Subscribe(string channelId, TimeSpan interval, Action<ChannelRuntimeValue> onValue);
    Task<ChannelRuntimeValue> ReadOnceAsync(string channelId, string owner, CancellationToken cancellationToken = default);
    Task WriteAsync(string channelId, JsonElement value, string owner, CancellationToken cancellationToken = default);
    Task<IDisposable> AcquireAutomationAsync(string owner, IEnumerable<string> channels, CancellationToken token = default) => Task.FromResult(AcquireAutomation(owner, channels));
    IDisposable AcquireAutomation(string owner, IEnumerable<string> channels);
}
