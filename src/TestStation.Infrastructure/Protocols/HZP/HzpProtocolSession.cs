using System.Buffers.Binary;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Protocols.HZP;

public sealed class HzpProtocolSession : IAsyncDisposable
{
    private readonly IByteTransportSession _transport; private readonly TransportResource _resource; private readonly byte _sensor; private readonly byte _host; private bool _disposed;
    public HzpProtocolSession(IByteTransportSession transport, TransportResource resource) { _transport = transport; _resource = resource; _sensor = Byte(resource.Settings, "sensor_address", 0xC1); _host = Byte(resource.Settings, "host_address", 0x01); }
    public async Task OpenAsync(CancellationToken token)
    { var settings = new Dictionary<string, System.Text.Json.JsonElement>(_resource.Settings, StringComparer.OrdinalIgnoreCase) { ["terminator"] = System.Text.Json.JsonSerializer.SerializeToElement(""), ["response_length"] = System.Text.Json.JsonSerializer.SerializeToElement(0x2F) }; await _transport.OpenAsync(new(_resource.Id, _resource.Type, settings), token).ConfigureAwait(false); }
    public async Task<float[]> ReadMeasurementsAsync(CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(_disposed, this); var request = new byte[] { 0x81, _sensor, _host, 0x0F, 0x82, 0x01, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }; request[^1] = Xor(request.AsSpan(0, request.Length - 1)); var response = await _transport.ExchangeFramedAsync(request, ByteResponseFraming.Fixed(0x2F), TimeSpan.FromMilliseconds(Int("timeout_ms", 1000)), token).ConfigureAwait(false); if (response.Length < 0x2F || response.Span[0] != 0x81 || response.Span[1] != _host || response.Span[2] != _sensor || response.Span[4] != 0x42 || response.Span[3] != response.Length || Xor(response.Span[..^1]) != response.Span[^1]) throw new InvalidDataException("HZP 响应校验失败"); var values = new float[8]; for (var i = 0; i < values.Length; i++) values[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(response.Span[(14 + i * 4)..])); return values; }
    private int Int(string key, int fallback) => _resource.Settings.TryGetValue(key, out var value) && value.TryGetInt32(out var result) && result > 0 ? result : fallback;
    private static byte Byte(IReadOnlyDictionary<string, System.Text.Json.JsonElement> settings, string key, byte fallback) => settings.TryGetValue(key, out var value) && value.TryGetInt32(out var result) ? (byte)Math.Clamp(result, 0, 255) : fallback;
    private static byte Xor(ReadOnlySpan<byte> data) { byte value = 0; foreach (var b in data) value ^= b; return value; }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _transport.CloseAsync().ConfigureAwait(false); await _transport.DisposeAsync().ConfigureAwait(false); }
}
