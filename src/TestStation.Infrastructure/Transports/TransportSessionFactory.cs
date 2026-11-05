using System.IO.Ports;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Channels;
using TestStation.Application.Drivers;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Transports.Can.Pcan;
using TestStation.Infrastructure.Transports.Can;

namespace TestStation.Infrastructure.Transports;

public sealed class TransportSessionFactory : ITransportSessionFactory
{
    private readonly IVisaBackend _visa;
    private readonly CanSessionPool _canPool = new();
    private readonly IReadOnlyDictionary<string, ICanVendorAdapter> _canAdapters;
    public TransportSessionFactory(IVisaBackend? visa = null, IEnumerable<ICanVendorAdapter>? canAdapters = null)
    {
        _visa = visa ?? new UnavailableVisaBackend();
        _canAdapters = (canAdapters ?? []).ToDictionary(item => item.BackendId, StringComparer.OrdinalIgnoreCase);
    }
    public IByteTransportSession CreateByte(TransportResource resource) => Create(resource) as IByteTransportSession
        ?? throw new NotSupportedException($"通信资源不支持字节流传输：{resource.Type}");
    public ICanBusSession CreateCan(TransportResource resource) => Create(resource) as ICanBusSession
        ?? throw new NotSupportedException($"通信资源不支持 CAN 总线传输：{resource.Type}");
    public ITransportSession Create(TransportResource resource) => TransportSettingsAccessor.NormalizeType(resource.Type) switch
    {
        "tcp" or "modbus_tcp" => new TcpTransportSession(),
        "udp" => new UdpTransportSession(),
        "serial" or "modbus_rtu" => new SerialTransportSession(),
        "visa" => new VisaTransportSession(_visa),
        "can" => _canPool.Create(CanEndpointKey, endpoint => CreateCanSession(new TransportResource { Type = "can", Settings = endpoint.Settings.ToDictionary(p => p.Key, p => p.Value) })),
        _ => throw new NotSupportedException($"不支持的通信资源类型：{resource.Type}")
    };

    private string CanEndpointKey(TransportEndpoint endpoint)
    {
        var backend = endpoint.Settings.TryGetValue("can_backend_id", out var b) ? b.ToString().ToLowerInvariant() : "";
        if (backend == "peak") backend = "pcan";
        var channel = endpoint.Settings.TryGetValue("channel", out var c) ? c.ToString() : "";
        if (_canAdapters.TryGetValue(backend, out var adapter)) channel = adapter.NormalizeChannel(channel);
        return backend + ":" + channel.ToUpperInvariant();
    }

    private ICanBusSession CreateCanSession(TransportResource resource)
    {
        var backend = resource.Settings.TryGetValue("can_backend_id", out var value) ? value.ToString() : "";
        if (backend.Equals("simulated", StringComparison.OrdinalIgnoreCase)) return new SimulatedCanBusSession();
        if (backend.Equals("pcan", StringComparison.OrdinalIgnoreCase) || backend.Equals("peak", StringComparison.OrdinalIgnoreCase))
        {
            if (_canAdapters.TryGetValue("pcan", out var adapter)) return new PcanCanBusSession((PcanVendorAdapter)adapter);
            return new CanTransportSession("PCAN-Basic 适配器未注册");
        }
        if (backend is "zlg" or "kvaser" or "vector" or "tosun" or "ni-xnet" or "etas")
        {
            var descriptor = TestStation.Infrastructure.Transports.Can.CanAdapterCatalog.Find(backend);
            var name = descriptor?.DisplayName ?? backend;
            return new CanTransportSession($"{name} 已登记，但当前版本尚未启用该厂商控制器会话；请先完成对应硬件适配和实机验收");
        }
        return new CanTransportSession(string.IsNullOrWhiteSpace(backend) ? "CAN 后端未配置；请选择 simulated 或 pcan" : $"CAN 后端未接入：{backend}");
    }
}

public abstract class TransportSessionBase : ITransportSession
{
    public string ResourceId { get; protected set; } = "";
    public string Type { get; }
    public TransportState State { get; protected set; } = TransportState.Closed;
    public event EventHandler? StateChanged;
    protected TransportSessionBase(string type) => Type = type;
    protected void SetState(TransportState value) { State = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    public abstract Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default);
    public abstract Task CloseAsync(CancellationToken cancellationToken = default);
    public abstract ValueTask DisposeAsync();
}

public abstract class ByteTransportSessionBase : TransportSessionBase, IByteTransportSession
{
    protected ByteTransportSessionBase(string type) : base(type) { }
    public abstract Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default);
    public virtual Task<ReadOnlyMemory<byte>> ExchangeFramedAsync(ReadOnlyMemory<byte> request, ByteResponseFraming framing, TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromException<ReadOnlyMemory<byte>>(new NotSupportedException("该传输后端尚未实现按帧接收"));
}

public sealed class TcpTransportSession : ByteTransportSessionBase
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _terminator = "\n";
    private int _responseLength;
    private int _idleReadMs = 20;
    private int _maxResponseBytes = 1024 * 1024;
    public TcpTransportSession() : base("tcp") { }

    public override async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceId = endpoint.ResourceId; SetState(TransportState.Opening); await CloseCoreAsync();
            var host = Text(endpoint.Settings, "host") ?? "127.0.0.1";
            var port = Number(endpoint.Settings, "tcp_port", 5025);
            var connectTimeout = Number(endpoint.Settings, "connect_timeout_ms", 1500);
            _terminator = Text(endpoint.Settings, "terminator") ?? "\n";
            _responseLength = endpoint.Settings.TryGetValue("response_length", out var responseLength) && int.TryParse(responseLength.ToString(), out var length) && length >= 0 ? length : 0;
            _idleReadMs = Number(endpoint.Settings, "idle_read_ms", 20);
            _maxResponseBytes = Number(endpoint.Settings, "max_response_bytes", 1024 * 1024);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(connectTimeout);
            try
            {
                _client = new TcpClient { NoDelay = true };
                await _client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                _stream = _client.GetStream(); SetState(TransportState.Open);
            }
            catch { await CloseCoreAsync(); SetState(TransportState.Faulted); throw; }
        }
        finally { _gate.Release(); }
    }

    public override Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default)
        => ExchangeCoreAsync(request, expectResponse, null, timeout, cancellationToken);

    public override async Task<ReadOnlyMemory<byte>> ExchangeFramedAsync(ReadOnlyMemory<byte> request, ByteResponseFraming framing, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await ExchangeCoreAsync(request, true, framing, timeout, cancellationToken).ConfigureAwait(false))!.Value;

    private async Task<ReadOnlyMemory<byte>?> ExchangeCoreAsync(ReadOnlyMemory<byte> request, bool expectResponse, ByteResponseFraming? framing, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new IOException("TCP 资源尚未打开");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
            try
            {
                await stream.WriteAsync(request, linked.Token).ConfigureAwait(false);
                if (!expectResponse) return null;
                if (framing is not null) return await ByteResponseReader.ReadAsync(stream, framing, _maxResponseBytes, linked.Token).ConfigureAwait(false);
                var result = new List<byte>(); var one = new byte[1]; var terminator = Encoding.ASCII.GetBytes(_terminator);
                while (true)
                {
                    if (await stream.ReadAsync(one, linked.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException("TCP 资源已断开");
                    result.Add(one[0]);
                    if (result.Count > _maxResponseBytes) throw new InvalidDataException("TCP 响应超过最大响应长度");
                    if (_responseLength > 0 && result.Count >= _responseLength) return result.Take(_responseLength).ToArray();
                    if (terminator.Length == 0)
                    {
                        await Task.Delay(_idleReadMs, linked.Token).ConfigureAwait(false);
                        while (stream.DataAvailable)
                        { if (await stream.ReadAsync(one, linked.Token).ConfigureAwait(false) == 0) break; result.Add(one[0]); if (result.Count > _maxResponseBytes) throw new InvalidDataException("TCP 响应超过最大响应长度"); }
                        return result.ToArray();
                    }
                    if (result.Count >= terminator.Length && result[^terminator.Length..].SequenceEqual(terminator)) return result.Take(result.Count - terminator.Length).ToArray();
                    if (result.Count > _maxResponseBytes) throw new InvalidDataException("TCP 响应超过最大响应长度");
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { await CloseCoreAsync(); SetState(TransportState.Faulted); throw new TimeoutException("TCP 通信超时"); }
            catch { await CloseCoreAsync(); SetState(TransportState.Faulted); throw; }
        }
        finally { _gate.Release(); }
    }

    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try { await CloseCoreAsync(); } finally { _gate.Release(); } }
    private Task CloseCoreAsync() { _stream?.Dispose(); _client?.Dispose(); _stream = null; _client = null; if (State != TransportState.Closed) SetState(TransportState.Closed); return Task.CompletedTask; }
    public override async ValueTask DisposeAsync() { try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Dispose(); } }
    private static string? Text(IReadOnlyDictionary<string, JsonElement> values, string key) => values.TryGetValue(key, out var v) ? v.ToString() : null;
    private static int Number(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback) => int.TryParse(Text(values, key), out var value) && value > 0 ? value : fallback;
}

public sealed class SerialTransportSession : ByteTransportSessionBase
{
    private SerialPort? _port;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _terminator = "\n";
    private int _responseLength;
    private int _idleReadMs = 20;
    private bool _halfDuplex;
    private int _directionDelayMs;
    private int _maxResponseBytes = 1024 * 1024;
    public SerialTransportSession() : base("serial") { }
    public override async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        SerialPort? serial = null;
        try
        {
            ResourceId = endpoint.ResourceId; SetState(TransportState.Opening); CloseCore();
            var port = Text(endpoint.Settings, "port") ?? throw new ArgumentException("串口资源缺少 port");
            _terminator = Text(endpoint.Settings, "terminator") ?? "\n";
            serial = new SerialPort(port, Number(endpoint.Settings, "baud_rate", 115200), ParseParity(Text(endpoint.Settings, "parity")), Number(endpoint.Settings, "data_bits", 8), ParseStopBits(Text(endpoint.Settings, "stop_bits")))
            { NewLine = string.IsNullOrEmpty(_terminator) ? "\n" : _terminator, ReadTimeout = Number(endpoint.Settings, "timeout_ms", 1500), WriteTimeout = Number(endpoint.Settings, "timeout_ms", 1500), Handshake = ParseHandshake(Text(endpoint.Settings, "flow_control")) };
            _halfDuplex = IsTrue(endpoint.Settings, "half_duplex"); _directionDelayMs = Number(endpoint.Settings, "direction_delay_ms", 0);
            _responseLength = OptionalNumber(endpoint.Settings, "response_length", 0); _idleReadMs = Number(endpoint.Settings, "idle_read_ms", 20);
            _maxResponseBytes = Number(endpoint.Settings, "max_response_bytes", 1024 * 1024);
            serial.Open(); _port = serial; serial = null; SetState(TransportState.Open);
        }
        catch { try { serial?.Dispose(); } catch { } SetState(TransportState.Faulted); throw; }
        finally { _gate.Release(); }
    }
    public override Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default)
        => ExchangeCoreAsync(request, expectResponse, null, timeout, cancellationToken);

    public override async Task<ReadOnlyMemory<byte>> ExchangeFramedAsync(ReadOnlyMemory<byte> request, ByteResponseFraming framing, TimeSpan timeout, CancellationToken cancellationToken = default)
        => (await ExchangeCoreAsync(request, true, framing, timeout, cancellationToken).ConfigureAwait(false))!.Value;

    private async Task<ReadOnlyMemory<byte>?> ExchangeCoreAsync(ReadOnlyMemory<byte> request, bool expectResponse, ByteResponseFraming? framing, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var port = _port ?? throw new IOException("串口资源尚未打开");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
            // Windows serial streams do not reliably cancel an outstanding read.
            using var abort = linked.Token.Register(() => { try { port.Dispose(); } catch { } });
            try
            {
                await port.BaseStream.WriteAsync(request, linked.Token).ConfigureAwait(false); await port.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
                if (_directionDelayMs > 0 && _halfDuplex) await Task.Delay(_directionDelayMs, linked.Token).ConfigureAwait(false);
                if (!expectResponse) return null;
                if (framing is not null) return await ByteResponseReader.ReadAsync(port.BaseStream, framing, _maxResponseBytes, linked.Token).ConfigureAwait(false);
                var result = new List<byte>(); var one = new byte[1]; var terminator = Encoding.ASCII.GetBytes(_terminator);
                while (true)
                {
                    if (await port.BaseStream.ReadAsync(one, linked.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException("串口已断开");
                    result.Add(one[0]); if (result.Count > _maxResponseBytes) throw new InvalidDataException("串口响应超过最大响应长度");
                    if (_responseLength > 0 && result.Count >= _responseLength) return result.Take(_responseLength).ToArray();
                    if (terminator.Length == 0)
                    { await Task.Delay(_idleReadMs, linked.Token).ConfigureAwait(false); while (port.BytesToRead > 0) { var read = port.BaseStream.ReadByte(); if (read < 0) break; result.Add((byte)read); if (result.Count > _maxResponseBytes) throw new InvalidDataException("串口响应超过最大响应长度"); } return result.ToArray(); }
                    if (result.Count >= terminator.Length && result[^terminator.Length..].SequenceEqual(terminator)) return result.Take(result.Count - terminator.Length).ToArray();
                }
            }
            catch (Exception error) when (linked.IsCancellationRequested)
            {
                CloseCore(); SetState(TransportState.Faulted);
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException("串口事务已取消", error, cancellationToken);
                throw new TimeoutException("串口通信超时", error);
            }
            catch { CloseCore(); SetState(TransportState.Faulted); throw; }
        }
        finally { _gate.Release(); }
    }
    public override async Task CloseAsync(CancellationToken cancellationToken = default) { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try { CloseCore(); } finally { _gate.Release(); } }
    private void CloseCore() { if (_port is not null) { try { _port.Close(); } catch { } _port.Dispose(); _port = null; } if (State != TransportState.Closed) SetState(TransportState.Closed); }
    public override async ValueTask DisposeAsync() { try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Dispose(); } }
    private static string? Text(IReadOnlyDictionary<string, JsonElement> values, string key) => values.TryGetValue(key, out var v) ? v.ToString() : null;
    private static int Number(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback) => int.TryParse(Text(values, key), out var value) && value > 0 ? value : fallback;
    private static Parity ParseParity(string? value) => Enum.TryParse<Parity>(value, true, out var result) ? result : Parity.None;
    private static StopBits ParseStopBits(string? value) => value switch { "1" => StopBits.One, "2" => StopBits.Two, "1.5" => StopBits.OnePointFive, _ => Enum.TryParse<StopBits>(value, true, out var result) ? result : StopBits.One };
    private static Handshake ParseHandshake(string? value) => value switch { "RtsCts" => Handshake.RequestToSend, "XOnXOff" => Handshake.XOnXOff, _ => Handshake.None };
    private static bool IsTrue(IReadOnlyDictionary<string, JsonElement> values, string key) => values.TryGetValue(key, out var value) && (value.ValueKind == JsonValueKind.True || bool.TryParse(value.ToString(), out var result) && result);
    private static int OptionalNumber(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback) => values.TryGetValue(key, out var value) && int.TryParse(value.ToString(), out var number) && number >= 0 ? number : fallback;
}

/// <summary>Datagram transport for devices whose protocol is explicitly UDP.</summary>
public sealed class UdpTransportSession : ByteTransportSessionBase
{
    private UdpClient? _client;
    private IPEndPoint? _remote;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public UdpTransportSession() : base("udp") { }
    public override async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceId = endpoint.ResourceId; SetState(TransportState.Opening); await CloseCoreAsync();
            var host = endpoint.Settings.TryGetValue("host", out var h) ? h.ToString() : "127.0.0.1";
            var port = endpoint.Settings.TryGetValue("udp_port", out var p) && p.TryGetInt32(out var parsed) ? parsed : endpoint.Settings.TryGetValue("tcp_port", out var tcpPort) && tcpPort.TryGetInt32(out var tcpParsed) ? tcpParsed : 5000;
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            _remote = new IPEndPoint(addresses.FirstOrDefault() ?? IPAddress.Loopback, port);
            _client = new UdpClient(); SetState(TransportState.Open);
        }
        catch { await CloseCoreAsync().ConfigureAwait(false); SetState(TransportState.Faulted); throw; }
        finally { _gate.Release(); }
    }
    public override async Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _client ?? throw new IOException("UDP 资源尚未打开"); var remote = _remote ?? throw new IOException("UDP 目标地址未配置");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
            await client.SendAsync(request.ToArray(), remote, linked.Token).ConfigureAwait(false); if (!expectResponse) return null;
            try { var result = await client.ReceiveAsync(linked.Token).ConfigureAwait(false); return result.Buffer; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { SetState(TransportState.Faulted); throw new TimeoutException("UDP 通信超时"); }
        }
        finally { _gate.Release(); }
    }
    public override async Task CloseAsync(CancellationToken cancellationToken = default) { await _gate.WaitAsync(cancellationToken).ConfigureAwait(false); try { await CloseCoreAsync().ConfigureAwait(false); } finally { _gate.Release(); } }
    private Task CloseCoreAsync() { _client?.Dispose(); _client = null; _remote = null; if (State != TransportState.Closed) SetState(TransportState.Closed); return Task.CompletedTask; }
    public override async ValueTask DisposeAsync() { try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Dispose(); } }
}

public interface IVisaBackend
{
    Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default);
    Task CloseAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>?> ExchangeAsync(TransportEndpoint endpoint, ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class VisaTransportSession : ByteTransportSessionBase
{
    private readonly IVisaBackend _backend; private TransportEndpoint? _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public VisaTransportSession(IVisaBackend backend) : base("visa") => _backend = backend;
    public override async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ResourceId = endpoint.ResourceId; SetState(TransportState.Opening);
            if (_endpoint is not null) { try { await _backend.CloseAsync(_endpoint, cancellationToken).ConfigureAwait(false); } finally { _endpoint = null; } }
            try { await _backend.OpenAsync(endpoint, cancellationToken).ConfigureAwait(false); _endpoint = endpoint; SetState(TransportState.Open); }
            catch { _endpoint = null; SetState(TransportState.Faulted); throw; }
        }
        finally { _gate.Release(); }
    }
    public override async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { var endpoint = _endpoint; _endpoint = null; if (endpoint is not null) await _backend.CloseAsync(endpoint, cancellationToken).ConfigureAwait(false); SetState(TransportState.Closed); }
        finally { _gate.Release(); }
    }
    public override async Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { var endpoint = _endpoint ?? throw new IOException("VISA 资源尚未打开"); return await _backend.ExchangeAsync(endpoint, request, expectResponse, timeout, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    public override async ValueTask DisposeAsync() { try { await CloseAsync().ConfigureAwait(false); } finally { _gate.Dispose(); } }
}

public sealed class UnavailableVisaBackend : IVisaBackend
{
    public Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException("VISA 后端未安装；请注册 NI-VISA、Keysight VISA 或其他 IVisaBackend 实现"));
    public Task CloseAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ReadOnlyMemory<byte>?> ExchangeAsync(TransportEndpoint endpoint, ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromException<ReadOnlyMemory<byte>?>(new NotSupportedException("VISA 后端未安装"));
}

public sealed class SimulatedVisaBackend : IVisaBackend
{
    private readonly Dictionary<string, Channel<byte[]>> _responses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    public void EnqueueResponse(string resourceName, ReadOnlyMemory<byte> response) { lock (_sync) { if (!_responses.TryGetValue(resourceName, out var queue)) _responses[resourceName] = queue = Channel.CreateUnbounded<byte[]>(); queue.Writer.TryWrite(response.ToArray()); } }
    public Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task CloseAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public async Task<ReadOnlyMemory<byte>?> ExchangeAsync(TransportEndpoint endpoint, ReadOnlyMemory<byte> request, bool expectResponse, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!expectResponse) return null;
        var name = endpoint.Settings.TryGetValue("visa_resource", out var value) ? value.ToString() : endpoint.ResourceId;
        Channel<byte[]> queue;
        lock (_sync) { if (!_responses.TryGetValue(name, out queue!)) _responses[name] = queue = Channel.CreateUnbounded<byte[]>(); }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); linked.CancelAfter(timeout);
        try { if (await queue.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false) && queue.Reader.TryRead(out var response)) return response; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("VISA 模拟后端响应超时"); }
        return null;
    }
}

public sealed class CanTransportSession : TransportSessionBase, ICanBusSession
{
    private readonly string _message;
    public CanTransportSession(string message = "CAN 后端未安装") : base("can") => _message = message;
    public override Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default) { ResourceId = endpoint.ResourceId; SetState(TransportState.Faulted); return Task.FromException(new NotSupportedException(_message)); }
    public override Task CloseAsync(CancellationToken cancellationToken = default) { SetState(TransportState.Closed); return Task.CompletedTask; }
    public Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default) => Task.FromException(new NotSupportedException(_message));
    public Task<CanFrame> SendAndReceiveAsync(CanFrame frame, CanFrameFilter expectedFilter, TimeSpan timeout, CancellationToken cancellationToken = default)
        => Task.FromException<CanFrame>(new NotSupportedException(_message));
    public long DroppedFrameCount => 0;
    public IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, CancellationToken cancellationToken = default)
        => ThrowUnavailable(cancellationToken);
    private async IAsyncEnumerable<CanFrame> ThrowUnavailable([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (cancellationToken.IsCancellationRequested) yield break;
        throw new NotSupportedException(_message);
    }
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>In-memory CAN/CAN-FD bus used by protocol and resource acceptance checks.</summary>
public sealed class SimulatedCanBusSession : TransportSessionBase, ICanBusSession
{
    private sealed class Bus
    {
        public readonly object Sync = new();
        public readonly HashSet<SimulatedCanBusSession> Sessions = [];
    }
    private static readonly ConcurrentDictionary<string, Bus> Buses = new(StringComparer.OrdinalIgnoreCase);
    private CanFrameInbox? _inbox;
    private Bus? _bus;
    private string _channel = "simulated";
    private bool _listenOnly;
    public SimulatedCanBusSession() : base("can") { }
    public override Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        if (_bus is not null) CloseAsync(cancellationToken).GetAwaiter().GetResult();
        _inbox = new CanFrameInbox();
        ResourceId = endpoint.ResourceId; _channel = endpoint.Settings.TryGetValue("channel", out var value) ? value.ToString() : endpoint.ResourceId;
        _listenOnly = endpoint.Settings.TryGetValue("listen_only", out var listen) && (listen.ValueKind == JsonValueKind.True || bool.TryParse(listen.ToString(), out var parsed) && parsed);
        _bus = Buses.GetOrAdd(_channel, _ => new()); lock (_bus.Sync) _bus.Sessions.Add(this); SetState(TransportState.Open); return Task.CompletedTask;
    }
    public override Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_bus is not null) { lock (_bus.Sync) _bus.Sessions.Remove(this); _bus = null; }
        _inbox?.Complete(); _inbox = null; SetState(TransportState.Closed); return Task.CompletedTask;
    }
    public Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
    {
        frame.Validate();
        if (_listenOnly) return Task.FromException(new InvalidOperationException("CAN 资源处于只听模式，不能发送帧"));
        if (_bus is null) return Task.FromException(new IOException("CAN 总线尚未打开"));
        var message = frame with { Timestamp = frame.Timestamp ?? DateTimeOffset.UtcNow };
        SimulatedCanBusSession[] recipients; lock (_bus.Sync) recipients = _bus.Sessions.ToArray(); foreach (var session in recipients) session._inbox?.Publish(message); return Task.CompletedTask;
    }
    public async Task<CanFrame> SendAndReceiveAsync(CanFrame frame, CanFrameFilter expectedFilter, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        var inbox = _inbox ?? throw new IOException("CAN 总线尚未打开");
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        using var subscription = inbox.Subscribe(expectedFilter, replayExisting: false);
        await SendAsync(frame, timeoutCancellation.Token).ConfigureAwait(false);
        return await subscription.ReadNextAsync(timeoutCancellation.Token).ConfigureAwait(false);
    }
    public long DroppedFrameCount => _inbox?.DroppedFrameCount ?? 0;
    public async IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_bus is null || _inbox is null) throw new IOException("CAN 总线尚未打开");
        using var subscription = _inbox.Subscribe(filter, replayExisting: true);
        await foreach (var frame in subscription.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return frame;
    }
    public override async ValueTask DisposeAsync() { await CloseAsync().ConfigureAwait(false); }
}
