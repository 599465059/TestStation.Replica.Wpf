using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Modbus;

namespace TestStation.Infrastructure.Protocols.Bp7330;

/// <summary>BP7330 uses Modbus RTU frames, including when carried over TCP.</summary>
public sealed class Bp7330Protocol : IAsyncDisposable
{
    public const byte DefaultAddress = 0x37;
    private readonly ModbusProtocolSession _protocol;
    public Bp7330Protocol(IByteTransportSession transport, TransportResource resource)
    {
        var settings = new Dictionary<string, JsonElement>(resource.Settings, StringComparer.OrdinalIgnoreCase);
        var address = settings.TryGetValue("address", out var value) ? value : JsonSerializer.SerializeToElement(DefaultAddress);
        settings["unit_id"] = address;
        var endpoint = new TransportResource { Id = resource.Id, Name = resource.Name, Type = resource.Type, Settings = settings };
        _protocol = new ModbusProtocolSession(transport, endpoint, useTcp: false);
    }
    public Task OpenAsync(CancellationToken token) => _protocol.OpenAsync(token);
    public async Task<ushort> ReadRegisterAsync(ushort address, CancellationToken token = default) =>
        (await _protocol.ReadInputAsync(address, 1, token).ConfigureAwait(false))[0];
    public Task WriteRegisterAsync(ushort address, ushort value, CancellationToken token = default) => _protocol.WriteSingleAsync(address, value, token);
    public Task WriteOutputAsync(bool enabled, CancellationToken token = default) => _protocol.WriteCoilAsync(1, enabled, token);
    public ValueTask DisposeAsync() => _protocol.DisposeAsync();
}
