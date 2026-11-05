using System.Buffers.Binary;
using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Protocols.Modbus;

/// <summary>Protocol framing and response validation for Modbus RTU/TCP.</summary>
public sealed class ModbusProtocolSession : IByteProtocolSession
{
    private readonly IByteTransportSession _transport;
    private readonly TransportResource _resource;
    private readonly byte _unitId;
    private readonly TimeSpan _timeout;
    private readonly bool _tcp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _transaction;
    private bool _disposed;

    public ModbusProtocolSession(IByteTransportSession transport, TransportResource resource, bool? useTcp = null)
    {
        _transport = transport;
        _resource = resource;
        _tcp = useTcp ?? TransportSettingsAccessor.NormalizeType(resource.Type) == "tcp";
        _unitId = Byte(resource.Settings, "unit_id", 1);
        _timeout = TimeSpan.FromMilliseconds(Int(resource.Settings, "timeout_ms", 1000));
    }

    public string ProtocolId => _tcp ? "modbus-tcp" : "modbus-rtu";
    public Task OpenAsync(CancellationToken cancellationToken = default) => _transport.OpenAsync(ToEndpoint(), cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken = default) => _transport.CloseAsync(cancellationToken);

    public async Task<ReadOnlyMemory<byte>> ExecuteAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var framing = _tcp
                ? new ByteResponseFraming(6, header =>
                {
                    var length = BinaryPrimitives.ReadUInt16BigEndian(header.Span[4..]);
                    if (length is < 3 or > 254) throw new InvalidDataException("Modbus MBAP 长度无效");
                    return 6 + length;
                })
                : new ByteResponseFraming(3, header => (header.Span[1] & 0x80) != 0 ? 5
                    : header.Span[1] is 3 or 4 ? 5 + header.Span[2]
                    : header.Span[1] is 5 or 6 ? 8
                    : throw new InvalidDataException("不支持的 Modbus 响应功能码"));
            var response = await _transport.ExchangeFramedAsync(request, framing, _timeout, cancellationToken).ConfigureAwait(false);
            ValidateResponse(request.Span, response.Span);
            return response;
        }
        finally { _gate.Release(); }
    }

    public Task<ushort[]> ReadHoldingAsync(ushort address, ushort count, CancellationToken token = default) => ReadAsync(3, address, count, token);
    public Task<ushort[]> ReadInputAsync(ushort address, ushort count, CancellationToken token = default) => ReadAsync(4, address, count, token);
    private async Task<ushort[]> ReadAsync(byte function, ushort address, ushort count, CancellationToken token)
    {
        if (count is 0 or > 125 || (int)address + count > 65536) throw new ArgumentOutOfRangeException(nameof(count));
        var response = await ExecuteAsync(BuildRequest(function, address, count), token).ConfigureAwait(false);
        var offset = _tcp ? 6 : 0;
        var bytes = count * 2;
        if (response.Span[offset + 2] != bytes || response.Length != offset + 3 + bytes + (_tcp ? 0 : 2))
            throw new InvalidDataException("Modbus 寄存器长度不匹配");
        var result = new ushort[count];
        for (var i = 0; i < count; i++) result[i] = BinaryPrimitives.ReadUInt16BigEndian(response.Span[(offset + 3 + i * 2)..]);
        return result;
    }

    public Task WriteSingleAsync(ushort address, ushort value, CancellationToken token = default) => ExecuteAsync(BuildRequest(6, address, value), token);
    public Task WriteCoilAsync(ushort address, bool value, CancellationToken token = default) => ExecuteAsync(BuildRequest(5, address, value ? (ushort)0xFF00 : (ushort)0), token);

    private byte[] BuildRequest(byte function, ushort address, ushort value)
    {
        var offset = _tcp ? 6 : 0;
        var frame = new byte[_tcp ? 12 : 8];
        frame[offset] = _unitId;
        frame[offset + 1] = function;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 2), address);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 4), value);
        if (_tcp)
        {
            BinaryPrimitives.WriteUInt16BigEndian(frame, unchecked((ushort)Interlocked.Increment(ref _transaction)));
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), 6);
        }
        else BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), Crc16(frame.AsSpan(0, 6)));
        return frame;
    }

    private void ValidateResponse(ReadOnlySpan<byte> request, ReadOnlySpan<byte> response)
    {
        var offset = _tcp ? 6 : 0;
        if (request.Length < offset + 2 || response.Length < offset + 3 + (_tcp ? 0 : 2))
            throw new InvalidDataException("Modbus 响应长度不足");
        if (_tcp)
        {
            if (!response[..2].SequenceEqual(request[..2])) throw new InvalidDataException("Modbus 事务标识不匹配");
            if (BinaryPrimitives.ReadUInt16BigEndian(response[2..]) != 0) throw new InvalidDataException("Modbus 协议标识无效");
            if (BinaryPrimitives.ReadUInt16BigEndian(response[4..]) != response.Length - 6)
                throw new InvalidDataException("Modbus MBAP 长度不匹配");
        }
        else if (Crc16(response[..^2]) != BinaryPrimitives.ReadUInt16LittleEndian(response[^2..]))
            throw new InvalidDataException("Modbus RTU CRC 校验失败");
        if (response[offset] != _unitId) throw new InvalidDataException("Modbus 从站地址不匹配");
        var function = request[offset + 1];
        if (response[offset + 1] == (function | 0x80))
        {
            if (response.Length != offset + 3 + (_tcp ? 0 : 2)) throw new InvalidDataException("Modbus 异常帧长度无效");
            throw new InvalidDataException($"Modbus 异常码 0x{response[offset + 2]:X2}");
        }
        if (response[offset + 1] != function) throw new InvalidDataException("Modbus 功能码不匹配");
        if (function is 5 or 6 && (response.Length != request.Length || !response.Slice(offset, 6).SequenceEqual(request.Slice(offset, 6))))
            throw new InvalidDataException("Modbus 写入确认地址或数值不匹配");
    }

    private TransportEndpoint ToEndpoint()
    {
        var settings = new Dictionary<string, JsonElement>(_resource.Settings, StringComparer.OrdinalIgnoreCase)
            { ["terminator"] = JsonSerializer.SerializeToElement("") };
        return new(_resource.Id, _resource.Type, settings);
    }
    private static ushort Crc16(ReadOnlySpan<byte> bytes) { ushort crc = 0xFFFF; foreach (var b in bytes) { crc ^= b; for (var i = 0; i < 8; i++) crc = (ushort)((crc & 1) == 1 ? (crc >> 1) ^ 0xA001 : crc >> 1); } return crc; }
    private static int Int(IReadOnlyDictionary<string, JsonElement> settings, string key, int fallback) => settings.TryGetValue(key, out var value) && int.TryParse(value.ToString(), out var number) && number > 0 ? number : fallback;
    private static byte Byte(IReadOnlyDictionary<string, JsonElement> settings, string key, byte fallback)
    {
        var number = Int(settings, key, fallback);
        if (number > 247) throw new InvalidDataException("Modbus 从站地址必须在 1～247 范围内");
        return (byte)number;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }
}
