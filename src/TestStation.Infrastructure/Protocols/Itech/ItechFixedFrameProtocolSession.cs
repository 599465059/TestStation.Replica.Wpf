using System.Buffers.Binary;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Protocols.Itech;

/// <summary>ITECH 26-byte fixed-frame protocol used by IT8211/IT8500.</summary>
public sealed class ItechFixedFrameProtocolSession : IAsyncDisposable
{
    private const int FrameLength = 26;
    private readonly IByteTransportSession _transport;
    private readonly TransportResource _resource;
    private readonly byte _address;
    private bool _disposed;
    public ItechFixedFrameProtocolSession(IByteTransportSession transport, TransportResource resource)
    { _transport = transport; _resource = resource; _address = (byte)Math.Clamp(ReadInt(resource.Settings, "address", 1), 1, 247); }
    public string ProtocolId => "itech-fixed";
    public async Task OpenAsync(CancellationToken token = default)
    {
        var settings = new Dictionary<string, System.Text.Json.JsonElement>(_resource.Settings, StringComparer.OrdinalIgnoreCase) { ["terminator"] = System.Text.Json.JsonSerializer.SerializeToElement(""), ["response_length"] = System.Text.Json.JsonSerializer.SerializeToElement(FrameLength) };
        await _transport.OpenAsync(new(_resource.Id, _resource.Type, settings), token).ConfigureAwait(false);
    }
    public Task CloseAsync(CancellationToken token = default) => _transport.CloseAsync(token);
    public async Task<byte[]> SendAsync(byte command, ReadOnlyMemory<byte> payload, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); var frame = Build(command, payload.Span); var response = await _transport.ExchangeFramedAsync(frame, ByteResponseFraming.Fixed(FrameLength), TimeSpan.FromMilliseconds(ReadInt(_resource.Settings, "timeout_ms", 1500)), token).ConfigureAwait(false);
        if (response.Length != FrameLength || response.Span[0] != 0xAA || response.Span[1] != _address || response.Span[2] != command || response.Span[25] != Checksum(response.Span)) throw new InvalidDataException($"ITECH 固定帧响应无效，命令 0x{command:X2}");
        return response.ToArray();
    }
    public Task<byte[]> ReadMeasurementAsync(bool load, CancellationToken token = default) => SendAsync(load ? (byte)0x5F : (byte)0x26, ReadOnlyMemory<byte>.Empty, token);
    public Task WriteVoltageAsync(double value, CancellationToken token = default) => SendAsync(0x2C, UInt32(value), token);
    public Task WriteCurrentAsync(double value, CancellationToken token = default) => SendAsync(0x2A, UInt32(value), token);
    public Task WriteOutputAsync(bool enabled, CancellationToken token = default) => SendAsync(0x21, new[] { (byte)(enabled ? 1 : 0) }, token);
    private byte[] Build(byte command, ReadOnlySpan<byte> payload) { var frame = new byte[FrameLength]; frame[0] = 0xAA; frame[1] = _address; frame[2] = command; payload[..Math.Min(22, payload.Length)].CopyTo(frame.AsSpan(3)); frame[25] = Checksum(frame); return frame; }
    private static byte[] UInt32(double value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)Math.Round(Math.Max(0, value) * 1000))); return bytes; }
    private static byte Checksum(ReadOnlySpan<byte> bytes) { var sum = 0; for (var i = 0; i < FrameLength - 1; i++) sum = (sum + bytes[i]) & 0xFF; return (byte)sum; }
    private static int ReadInt(IReadOnlyDictionary<string, System.Text.Json.JsonElement> values, string key, int fallback) => values.TryGetValue(key, out var value) && value.TryGetInt32(out var result) && result > 0 ? result : fallback;
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await CloseAsync().ConfigureAwait(false); await _transport.DisposeAsync().ConfigureAwait(false); }
}
