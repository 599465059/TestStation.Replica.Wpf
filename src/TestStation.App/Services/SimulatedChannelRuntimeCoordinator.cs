using System.Text.Json;
using TestStation.Application;

namespace TestStation.App.Services;

/// <summary>
/// One polling scheduler and one automation lease for all sample plugins.
/// Manual views only subscribe; they never own timers or call a driver
/// directly.
/// </summary>
public sealed class SimulatedChannelRuntimeCoordinator : IChannelRuntimeCoordinator, IDisposable
{
    private sealed class Subscription : IDisposable
    {
        private readonly SimulatedChannelRuntimeCoordinator _owner;
        internal readonly string Channel;
        internal readonly Action<ChannelRuntimeValue> Callback;
        private int _disposed;

        public Subscription(SimulatedChannelRuntimeCoordinator owner, string channel, Action<ChannelRuntimeValue> callback)
        {
            _owner = owner; Channel = channel; Callback = callback;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Remove(this);
        }
    }

    private sealed class Poller
    {
        public readonly string Channel;
        public readonly TimeSpan Interval;
        public readonly CancellationTokenSource Stop = new();
        public readonly List<Action<ChannelRuntimeValue>> Callbacks = [];
        public Poller(string channel, TimeSpan interval) { Channel = channel; Interval = interval; }
    }

    private sealed class AutomationLease : IDisposable
    {
        private readonly SimulatedChannelRuntimeCoordinator _owner;
        private int _disposed;
        public AutomationLease(SimulatedChannelRuntimeCoordinator owner) => _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.ReleaseAutomation();
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Poller> _pollers = new(StringComparer.OrdinalIgnoreCase);
    private int _automationLeases;
    private bool _disposed;

    public SimulatedChannelRuntimeCoordinator(ISequenceChannelProvider provider)
    {
        foreach (var pair in provider.GetDefaultValues()) _values[pair.Key] = pair.Value.Clone();
    }

    public bool IsAutomationActive { get { lock (_sync) return _automationLeases > 0; } }
    public event EventHandler? AvailabilityChanged;

    public IDisposable Subscribe(string channelId, TimeSpan interval, Action<ChannelRuntimeValue> onValue)
    {
        if (string.IsNullOrWhiteSpace(channelId)) throw new ArgumentException("通道 ID 不能为空", nameof(channelId));
        if (interval < TimeSpan.FromMilliseconds(50)) interval = TimeSpan.FromMilliseconds(50);
        ArgumentNullException.ThrowIfNull(onValue);
        var subscription = new Subscription(this, channelId, onValue);
        lock (_sync)
        {
            if (!_pollers.TryGetValue(channelId, out var poller))
            {
                poller = new Poller(channelId, interval); _pollers[channelId] = poller;
                _ = RunPollerAsync(poller);
            }
            poller.Callbacks.Add(onValue);
        }
        return subscription;
    }

    private async Task RunPollerAsync(Poller poller)
    {
        while (!poller.Stop.IsCancellationRequested)
        {
            if (!IsAutomationActive)
            {
                try
                {
                    var value = await ReadOnceAsync(poller.Channel, "manual-poll", poller.Stop.Token);
                    Action<ChannelRuntimeValue>[] callbacks;
                    lock (_sync) callbacks = poller.Callbacks.ToArray();
                    foreach (var callback in callbacks)
                        try { callback(value); } catch { }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
            try { await Task.Delay(poller.Interval, poller.Stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public Task<ChannelRuntimeValue> ReadOnceAsync(string channelId, string owner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_automationLeases > 0 && !owner.Equals("automatic", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自动测试正在占用设备，手动读取已暂停");
            if (!_values.TryGetValue(channelId, out var value))
                throw new KeyNotFoundException($"模拟通道不存在：{channelId}");
            return Task.FromResult(new ChannelRuntimeValue(channelId, value.Clone(), DateTimeOffset.Now));
        }
    }

    public Task WriteAsync(string channelId, JsonElement value, string owner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_automationLeases > 0 && !owner.Equals("automatic", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自动测试正在占用设备，手动写入已暂停");
            _values[channelId] = value.Clone();
        }
        return Task.CompletedTask;
    }

    public IDisposable AcquireAutomation(string owner, IEnumerable<string> channels)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimulatedChannelRuntimeCoordinator));
            _automationLeases++;
        }
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        return new AutomationLease(this);
    }

    private void ReleaseAutomation()
    {
        lock (_sync) _automationLeases = Math.Max(0, _automationLeases - 1);
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            if (!_pollers.TryGetValue(subscription.Channel, out var poller)) return;
            poller.Callbacks.Remove(subscription.Callback);
            if (poller.Callbacks.Count == 0) { poller.Stop.Cancel(); _pollers.Remove(subscription.Channel); }
        }
    }

    public void Dispose()
    {
        Poller[] pollers;
        lock (_sync) { _disposed = true; pollers = _pollers.Values.ToArray(); _pollers.Clear(); }
        foreach (var poller in pollers) poller.Stop.Cancel();
    }
}
