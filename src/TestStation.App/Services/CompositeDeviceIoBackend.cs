using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Drivers;

namespace TestStation.App.Services;

/// <summary>Routes device channels to their family service while keeping one coordinator.</summary>
public sealed class CompositeDeviceIoBackend : IAutomationChannelBackend
{
    private readonly IReadOnlyList<IAutomationChannelBackend> _backends;
    public CompositeDeviceIoBackend(params IAutomationChannelBackend[] backends) => _backends = backends.Where(item => item is not null).ToArray();
    public IReadOnlyList<string> ChannelIds => _backends.SelectMany(item => item.ChannelIds).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public bool IsAutomationActive { get => _backends.Any(item => item.IsAutomationActive); set { foreach (var item in _backends) item.IsAutomationActive = value; } }
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => _backends.SelectMany(item => item.GetChannels()).ToArray();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
    public bool OwnsChannel(string channelId) => _backends.Any(item => item.OwnsChannel(channelId));
    private IAutomationChannelBackend Resolve(string channelId) => _backends.FirstOrDefault(item => item.OwnsChannel(channelId)) ?? throw new InvalidOperationException($"设备通道不可用：{channelId}");
    public Task<JsonElement> ReadAsync(string channelId, CancellationToken token = default) => Resolve(channelId).ReadAsync(channelId, token);
    public Task WriteAsync(string channelId, JsonElement value, CancellationToken token = default) => Resolve(channelId).WriteAsync(channelId, value, token);
    public Task DisconnectAsync(CancellationToken token = default) => Task.WhenAll(_backends.Select(item => item.DisconnectAsync(token)));
    public Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken token = default) =>
        AutomationBackendLifecycle.BeginAsync(_backends, channels, token);
    public Task EndAutomationAsync(CancellationToken token = default) => AutomationBackendLifecycle.EndAsync(_backends);
    public ValueTask DisposeAsync() => new(AutomationBackendLifecycle.DisposeAsync(_backends));
}
