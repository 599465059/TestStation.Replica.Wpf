using System.Globalization;
using System.Text;
using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Protocols.Scpi;

/// <summary>SCPI message rules shared by TCP, serial and VISA sessions.</summary>
public sealed class ScpiProtocolSession : IProtocolSession
{
    public sealed record TrafficRecord(DateTimeOffset Time, string Direction, string Text);
    public event EventHandler<TrafficRecord>? Traffic;
    private readonly IByteTransportSession _transport;
    private readonly TransportEndpoint _endpoint;
    private readonly TimeSpan _timeout;
    private bool _disposed;
    public string ProtocolId => "scpi";

    public ScpiProtocolSession(IByteTransportSession transport, TransportEndpoint endpoint)
    {
        _transport = transport; _endpoint = endpoint;
        _timeout = TimeSpan.FromMilliseconds(ReadInt(endpoint.Settings, "timeout_ms", ReadInt(endpoint.Settings, "connect_timeout_ms", 1500)));
    }

    public Task OpenAsync(CancellationToken cancellationToken = default) => _transport.OpenAsync(_endpoint, cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken = default) => _transport.CloseAsync(cancellationToken);

    public async Task SendAsync(string command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(command);
        var request = Encode(command);
        Traffic?.Invoke(this, new(DateTimeOffset.UtcNow, "TX", command));
        await _transport.ExchangeAsync(request, false, _timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> QueryAsync(string command, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Validate(command);
        var request = Encode(command);
        Traffic?.Invoke(this, new(DateTimeOffset.UtcNow, "TX", command));
        var response = await _transport.ExchangeAsync(request, true, _timeout, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("SCPI 查询没有返回数据");
        var text = Encoding.ASCII.GetString(response.Span).TrimEnd('\r', '\n');
        Traffic?.Invoke(this, new(DateTimeOffset.UtcNow, "RX", text));
        return text;
    }

    private static void Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(c => c is '\r' or '\n' || c > 127))
            throw new ArgumentException("SCPI 指令必须是非空 ASCII 单行文本", nameof(command));
    }
    private byte[] Encode(string command) => Encoding.ASCII.GetBytes(command + (_endpoint.Settings.TryGetValue("terminator", out var value) ? value.GetString() ?? "\n" : "\n"));
    private static int ReadInt(IReadOnlyDictionary<string, JsonElement> values, string key, int fallback) => values.TryGetValue(key, out var value) && value.TryGetInt32(out var result) && result > 0 ? result : fallback;
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _transport.DisposeAsync().ConfigureAwait(false); }
}

public static class ScpiEndpointFactory
{
    public static TransportEndpoint From(ScpiEndpoint endpoint, string resourceId = "scpi") => new(resourceId, "tcp", new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = JsonSerializer.SerializeToElement(endpoint.Host),
        ["tcp_port"] = JsonSerializer.SerializeToElement(endpoint.Port),
        ["connect_timeout_ms"] = JsonSerializer.SerializeToElement(endpoint.TimeoutMs),
        ["timeout_ms"] = JsonSerializer.SerializeToElement(endpoint.TimeoutMs),
        ["terminator"] = JsonSerializer.SerializeToElement("\n")
    });
}
