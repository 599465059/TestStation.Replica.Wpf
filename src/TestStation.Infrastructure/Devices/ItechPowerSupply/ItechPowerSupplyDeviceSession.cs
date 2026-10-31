using System.Text.Json;
using TestStation.Application.Hardware;

namespace TestStation.Infrastructure.Devices.ItechPowerSupply;

public sealed class ItechPowerSupplyDeviceSession : IDeviceSession
{
    private readonly IProtocolSession _protocol;
    private readonly IDeviceDriver _driver;
    private bool _disposed;
    public ItechPowerSupplyDeviceSession(string deviceId, string resourceId, IProtocolSession protocol, IDeviceDriver driver)
    { DeviceId = deviceId; ResourceId = resourceId; _protocol = protocol; _driver = driver; }
    public string DeviceId { get; }
    public string ResourceId { get; }
    public bool IsConnected { get; private set; }
    public async Task ConnectAsync(CancellationToken cancellationToken = default) { ObjectDisposedException.ThrowIf(_disposed, this); await _protocol.OpenAsync(cancellationToken).ConfigureAwait(false); IsConnected = true; }
    public async Task DisconnectAsync(CancellationToken cancellationToken = default) { if (!IsConnected) return; await _protocol.CloseAsync(cancellationToken).ConfigureAwait(false); IsConnected = false; }
    private void EnsureConnected() { ObjectDisposedException.ThrowIf(_disposed, this); if (!IsConnected) throw new IOException("设备未连接"); }
    public Task<string> QueryAsync(string command, CancellationToken cancellationToken = default) { EnsureConnected(); return _protocol.QueryAsync(command, cancellationToken); }
    public Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default) { EnsureConnected(); return _driver.ReadAsync(channelId, cancellationToken); }
    public Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default) { EnsureConnected(); return _driver.WriteAsync(channelId, value, cancellationToken); }
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _driver.DisposeAsync().ConfigureAwait(false); IsConnected = false; }
}
