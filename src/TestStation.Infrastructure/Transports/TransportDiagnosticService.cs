using TestStation.Application;
using TestStation.Application.Hardware;

namespace TestStation.Infrastructure.Transports;

/// <summary>只使用正式传输工厂；临时诊断也必须获取资源及运行时租约。</summary>
public sealed class TransportDiagnosticService(IResourceManager resources, ITransportSessionFactory factory, IChannelRuntimeCoordinator runtime) : ITransportDiagnosticService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ITransportSession? _session;
    private IDisposable? _resourceLease;
    private IDisposable? _runtimeLease;
    public bool IsOpen => _session?.State == TestStation.Application.Drivers.TransportState.Open;
    public string ResourceId => _session?.ResourceId ?? "";
    public bool IsCan => _session is ICanBusSession;
    public event EventHandler<string>? Traffic;
    public async Task OpenAsync(string resourceId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_session is not null) throw new InvalidOperationException("请先关闭当前诊断连接");
            var resource = resources.Find(resourceId) ?? throw new InvalidOperationException("通信资源不存在");
            _runtimeLease = await runtime.AcquireDiagnosticAsync(token).ConfigureAwait(false);
            try
            {
                // 诊断独占端点，避免原始报文干扰设备驱动事务。
                _resourceLease = resources.Acquire(resourceId, "diagnostic:" + Guid.NewGuid().ToString("N"), ResourceLeaseMode.Exclusive);
                _session = factory.Create(resource);
                await _session.OpenAsync(new(resource.Id, resource.Type, resource.Settings, ResourceLeaseMode.Exclusive), token).ConfigureAwait(false);
                Traffic?.Invoke(this, "OPEN " + resource.Name);
            }
            catch { await CloseCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    public async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task CloseCoreAsync()
    {
        var session = _session; _session = null;
        try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
        finally { _resourceLease?.Dispose(); _resourceLease = null; _runtimeLease?.Dispose(); _runtimeLease = null; }
    }
    private async Task<T> RunAsync<T>(Func<ITransportSession, Task<T>> operation, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _session is null) throw new IOException("请先打开诊断连接");
            try { return await operation(_session).ConfigureAwait(false); }
            catch { await CloseCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    public Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> bytes, bool response, TimeSpan timeout, CancellationToken token = default)
        => RunAsync(async session =>
        {
            var transport = session as IByteTransportSession ?? throw new NotSupportedException("所选资源不是字节流传输");
            Traffic?.Invoke(this, "TX " + Convert.ToHexString(bytes.Span));
            var value = await transport.ExchangeAsync(bytes, response, timeout, token).ConfigureAwait(false);
            if (value.HasValue) Traffic?.Invoke(this, "RX " + Convert.ToHexString(value.Value.Span));
            return value;
        }, token);
    public async Task SendCanAsync(CanFrame frame, CancellationToken token = default)
        => await RunAsync(async session =>
        {
            var can = session as ICanBusSession ?? throw new NotSupportedException("所选资源不是 CAN 总线");
            await can.SendAsync(frame, token).ConfigureAwait(false);
            Traffic?.Invoke(this, $"TX {frame.Id:X} {Convert.ToHexString(frame.Data.Span)}");
            return true;
        }, token).ConfigureAwait(false);
    public Task<CanFrame> ReadCanAsync(CanFrameFilter filter, TimeSpan timeout, CancellationToken token = default)
        => RunAsync(async session =>
        {
            var can = session as ICanBusSession ?? throw new NotSupportedException("所选资源不是 CAN 总线");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token); stop.CancelAfter(timeout);
            var frame = await can.ReceiveAsync(filter, stop.Token).FirstAsync(stop.Token).ConfigureAwait(false);
            Traffic?.Invoke(this, $"RX {frame.Id:X} {Convert.ToHexString(frame.Data.Span)} historyEvicted={can.DroppedFrameCount}");
            return frame;
        }, token);
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
