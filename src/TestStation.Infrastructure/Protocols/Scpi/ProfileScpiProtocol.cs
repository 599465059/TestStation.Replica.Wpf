using System.Text;
using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Protocols.Scpi;

/// <summary>SCPI framing and per-command timing, independent of device capabilities.</summary>
internal sealed class ProfileScpiProtocol(IByteTransportSession transport, TransportResource resource,
    string terminator, bool upper, int delayMs, Action<string, string> traffic) : IAsyncDisposable
{
    public Task OpenAsync(CancellationToken token)
    {
        var settings = resource.Settings.ToDictionary(p => p.Key, p => p.Value);
        settings["terminator"] = JsonSerializer.SerializeToElement(terminator);
        return transport.OpenAsync(new(resource.Id, resource.Type, settings), token);
    }

    public async Task<string?> ExecuteAsync(string command, bool query, int timeoutMs, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Any(c => c < 32 || c > 126))
            throw new InvalidDataException("SCPI 命令必须为非空 ASCII 单行文本");
        if (upper) command = command.ToUpperInvariant();
        if (delayMs > 0) await Task.Delay(delayMs, token).ConfigureAwait(false);
        traffic("TX", command);
        var result = await transport.ExchangeAsync(Encoding.ASCII.GetBytes(command + terminator), query,
            TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1, 120000)), token).ConfigureAwait(false);
        if (!query) return null;
        if (!result.HasValue) throw new InvalidDataException("SCPI 查询未返回响应");
        var text = Encoding.ASCII.GetString(result.Value.Span).Trim();
        traffic("RX", text);
        return text;
    }
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
