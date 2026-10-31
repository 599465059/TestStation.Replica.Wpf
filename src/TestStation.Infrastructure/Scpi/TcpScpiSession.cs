using System.Net.Sockets;
using System.Text;
using TestStation.Application.Drivers;

namespace TestStation.Infrastructure.Scpi;

/// <summary>ASCII, LF-framed SCPI. A failed exchange always invalidates the stream.</summary>
internal sealed class TcpScpiSession : IAsyncDisposable
{
    private TcpClient? _client;
    private ScpiEndpoint _endpoint = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public bool IsConnected => _client is not null;

    public async Task ConnectAsync(ScpiEndpoint endpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Close();
            if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is < 1 or > 65535 || endpoint.TimeoutMs is < 100 or > 60000)
                throw new ArgumentException("SCPI 地址、端口或超时参数无效");
            _endpoint = endpoint;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(endpoint.TimeoutMs);
            var client = new TcpClient { NoDelay = true };
            try { await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token).ConfigureAwait(false); _client = client; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { client.Dispose(); throw new TimeoutException($"连接 SCPI 设备超时：{endpoint.Host}:{endpoint.Port}"); }
            catch { client.Dispose(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> ExchangeAsync(string command, bool query, CancellationToken cancellationToken)
    {
        if (command.Any(c => c is '\r' or '\n' || c > 127)) throw new ArgumentException("SCPI 指令必须是单行 ASCII 文本");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var stream = (_client ?? throw new IOException("SCPI 设备尚未连接")).GetStream();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_endpoint.TimeoutMs);
            try
            {
                await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), timeout.Token).ConfigureAwait(false);
                if (!query) return null; // Settings do not return an ACK.
                var response = new List<byte>();
                var one = new byte[1];
                while (true)
                {
                    if (await stream.ReadAsync(one, timeout.Token).ConfigureAwait(false) == 0) throw new EndOfStreamException("SCPI 设备已断开连接");
                    if (one[0] == '\n') return Encoding.ASCII.GetString(response.ToArray()).TrimEnd('\r');
                    if (one[0] > 127 || response.Count >= 16384) throw new InvalidDataException("SCPI 响应无效或超长");
                    response.Add(one[0]);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { Close(); throw new TimeoutException($"SCPI 查询超时：{command}"); }
            catch { Close(); throw; }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Close(); }
        finally { _gate.Release(); }
    }

    private void Close() { _client?.Dispose(); _client = null; }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; Close(); }
        finally { _gate.Release(); }
    }
}
