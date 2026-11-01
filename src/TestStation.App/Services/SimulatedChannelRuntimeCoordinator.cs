using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Drivers;

namespace TestStation.App.Services;

/// <summary>
/// One polling scheduler and one automation lease for all sample plugins.
/// Manual views only subscribe; they never own timers or call a driver
/// directly.
/// </summary>
public sealed class SimulatedChannelRuntimeCoordinator : IChannelRuntimeCoordinator, IExternalChannelRuntime, IDisposable
{
    private sealed class EmptyDeviceBackend : IChannelIoBackend
    {
        public IReadOnlyList<string> ChannelIds { get; } = Array.Empty<string>();
        public Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default) =>
            Task.FromException<JsonElement>(new InvalidOperationException($"设备通道不可用：{channelId}"));
        public Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException($"设备通道不可用：{channelId}"));
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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

    private sealed class AutomationLease : IDisposable, IAsyncDisposable
    {
        private readonly SimulatedChannelRuntimeCoordinator _owner;
        private int _disposed;
        public AutomationLease(SimulatedChannelRuntimeCoordinator owner) => _owner = owner;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _ = _owner.ReleaseAutomationAsync(); }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) await _owner.ReleaseAutomationAsync().ConfigureAwait(false);
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Poller> _pollers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ISequenceChannelProvider _provider;
    private readonly IChannelIoBackend _io;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private int _automationLeases;
    private int _diagnosticLeases;
    private bool _disposed;

    public SimulatedChannelRuntimeCoordinator(ISequenceChannelProvider provider, IChannelIoBackend? io = null)
    {
        _provider = provider; _io = io ?? new EmptyDeviceBackend();
        foreach (var pair in provider.GetDefaultValues()) _values[pair.Key] = pair.Value.Clone();
        if (_provider is ISequenceChannelChangeSource changes) changes.Changed += ProviderChanged;
    }

    public IReadOnlyList<string> ExternalChannelIds => _io.ChannelIds;
    private IAutomationChannelBackend? Devices => _io as IAutomationChannelBackend;
    public bool IsExternalChannel(string id) => Devices?.OwnsChannel(id) == true || _io.ChannelIds.Contains(id, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> ExternalChannelTypes => Devices?.GetChannels().ToDictionary(c => c.Id, c => c.DataType, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, string>();
    private bool IsExternal(string id) => IsExternalChannel(id);
    private async void ProviderChanged(object? sender, EventArgs e)
    {
        try
        {
            if (Devices is not null)
            {
                var enabled = _provider.GetChannels().Any(c => IsExternal(c.Id));
                if (!enabled) { await Devices.DisconnectAsync().ConfigureAwait(false); }
                return;
            }
            return;
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
    }

    private SequenceChannelDefinition GetAvailableChannel(string id)
    {
        var channel = _provider.GetChannels().FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"通道不可用或插件已禁用：{id}");
        if (!_values.ContainsKey(id) && _provider.GetDefaultValues().TryGetValue(id, out var value)) _values[id] = value.Clone();
        return channel;
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
            ObjectDisposedException.ThrowIf(_disposed, this);
            GetAvailableChannel(channelId);
            if (!_pollers.TryGetValue(channelId, out var poller))
            {
                poller = new Poller(channelId, interval); _pollers[channelId] = poller;
                poller.Callbacks.Add(onValue);
                _ = Task.Run(() => RunPollerAsync(poller));
            }
            else poller.Callbacks.Add(onValue);
        }
        return subscription;
    }

    private async Task RunPollerAsync(Poller poller)
    {
        try
        {
            while (!poller.Stop.IsCancellationRequested)
            {
                if (!IsAutomationActive)
                {
                    try
                    {
                        var value = await ReadOnceAsync(poller.Channel, "manual-poll", poller.Stop.Token);
                        PublishPoll(poller, value);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception error)
                    { PublishPoll(poller, new(poller.Channel, JsonSerializer.SerializeToElement<object?>(null), DateTimeOffset.Now, error.Message)); }
                }
                try { await Task.Delay(poller.Interval, poller.Stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally { poller.Stop.Dispose(); }
    }

    private void PublishPoll(Poller poller, ChannelRuntimeValue value)
    {
        Action<ChannelRuntimeValue>[] callbacks;
        lock (_sync)
        {
            if (_disposed || _automationLeases > 0 || poller.Stop.IsCancellationRequested) return;
            callbacks = poller.Callbacks.ToArray();
        }
        foreach (var callback in callbacks) try { callback(value); } catch { }
    }

    public async Task<ChannelRuntimeValue> ReadOnceAsync(string channelId, string owner,
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!GetAvailableChannel(channelId).CanRead) throw new InvalidOperationException($"通道不可读：{channelId}");
                if (_automationLeases > 0 && !owner.Equals("automatic", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("自动测试正在占用设备，手动读取已暂停");
                if (!IsExternal(channelId))
                {
                    if (!_values.TryGetValue(channelId, out var value)) throw new KeyNotFoundException($"模拟通道不存在：{channelId}");
                    return new ChannelRuntimeValue(channelId, value.Clone(), DateTimeOffset.Now);
                }
            }
            var actual = await _io.ReadAsync(channelId, cancellationToken).ConfigureAwait(false);
            return new ChannelRuntimeValue(channelId, actual, DateTimeOffset.Now);
        }
        finally { _ioGate.Release(); }
    }

    public async Task WriteAsync(string channelId, JsonElement value, string owner,
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var channel = GetAvailableChannel(channelId);
                if (!channel.CanWrite) throw new InvalidOperationException($"通道只读：{channelId}");
                value = JsonSerializer.SerializeToElement(SequenceValueOperations.ConvertTo(HostValue(value), channel.DataType));
                if (_automationLeases > 0 && !owner.Equals("automatic", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("自动测试正在占用设备，手动写入已暂停");
                if (!IsExternal(channelId)) { _values[channelId] = value.Clone(); return; }
            }
            await _io.WriteAsync(channelId, value, cancellationToken).ConfigureAwait(false);
        }
        finally { _ioGate.Release(); }
    }

    private static object? HostValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(), JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Array => value.EnumerateArray().Select(HostValue).ToArray(), _ => null
    };

    public async Task<IDisposable> AcquireAutomationAsync(string owner, IEnumerable<string> channels, CancellationToken token = default)
    {
        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_diagnosticLeases > 0) throw new InvalidOperationException("请先关闭通信诊断，再运行自动测试");
                if (_automationLeases > 0) throw new InvalidOperationException("已有自动测试正在运行");
            }
            var requested = channels.ToArray();
            try
            {
                if (Devices is not null) await Devices.BeginAutomationAsync(requested, token).ConfigureAwait(false);
                return AcquireAutomationCore(owner, requested);
            }
            catch { if (Devices is not null) await Devices.EndAutomationAsync(CancellationToken.None).ConfigureAwait(false); throw; }
        }
        finally { _ioGate.Release(); }
    }
    public async Task ExecuteManualAsync(Func<Task> operation, CancellationToken token = default)
    {
        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_automationLeases > 0) throw new InvalidOperationException("自动测试占用期间禁止修改连接");
            }
            await operation().ConfigureAwait(false);
        }
        finally { _ioGate.Release(); }
    }
    public async Task<IDisposable> AcquireDiagnosticAsync(CancellationToken token = default)
    {
        await _ioGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_automationLeases > 0) throw new InvalidOperationException("自动测试运行期间禁止打开通信诊断");
                _diagnosticLeases++;
            }
            return new DiagnosticLease(this);
        }
        finally { _ioGate.Release(); }
    }
    private sealed class DiagnosticLease(SimulatedChannelRuntimeCoordinator owner) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) lock (owner._sync) owner._diagnosticLeases--; }
    }
    public IDisposable AcquireAutomation(string owner, IEnumerable<string> channels) => AcquireAutomationAsync(owner, channels).GetAwaiter().GetResult();

    private IDisposable AcquireAutomationCore(string owner, IEnumerable<string> channels)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SimulatedChannelRuntimeCoordinator));
            _automationLeases++;
        }
        // A software-only provider still needs the same lease semantics so
        // manual polling pauses while OpenTAP owns the runtime. A device
        // service additionally opens the referenced physical sessions.
        try { if (Devices is not null) Devices.IsAutomationActive = true; }
        catch { lock (_sync) _automationLeases = Math.Max(0, _automationLeases - 1); throw; }
        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
        return new AutomationLease(this);
    }

    private async Task ReleaseAutomationAsync()
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Manual access must remain paused until temporary sessions have been released.
            if (Devices is not null) await Devices.EndAutomationAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _automationLeases = Math.Max(0, _automationLeases - 1);
            try { if (Devices is not null) Devices.IsAutomationActive = false; }
            finally { _ioGate.Release(); AvailabilityChanged?.Invoke(this, EventArgs.Empty); }
        }
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
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_provider is ISequenceChannelChangeSource changes) changes.Changed -= ProviderChanged;
            foreach (var poller in _pollers.Values) poller.Stop.Cancel();
            _pollers.Clear();
        }
        _io.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
