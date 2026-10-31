using System.Collections.Concurrent;
using System.Threading.Channels;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.Application.Drivers;

public enum TransportState { Closed, Opening, Open, Faulted }
public sealed record TransportFrame(DateTimeOffset Timestamp, byte[] Payload, string Key = "");
public sealed record ChannelReading(string DeviceId, string ChannelId, object? Value, ChannelQuality Quality, DateTimeOffset Timestamp, string Error = "", object? LastSuccessfulValue = null, DateTimeOffset? LastSuccessfulTimestamp = null, bool IsReading = false);
public enum ChannelQuality { Unknown, Good, Timeout, Error, Stale }
public sealed class ChannelValueUpdatedEventArgs(ChannelReading reading) : EventArgs { public ChannelReading Reading { get; } = reading; }
public sealed record PollingStatus(string DeviceId, bool IsRunning, int ActiveChannelCount, DateTimeOffset? StartedAt = null, string Error = "");
public sealed record DriverDescriptor(string Id, string Name, string Version, IReadOnlyList<string> TransportTypes, IReadOnlyList<string> Protocols, string Description, bool IsAvailable = true);

public interface ITransport : IAsyncDisposable
{
    string Id { get; }
    TransportState State { get; }
    event EventHandler? StateChanged;
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task<TransportFrame?> TransactAsync(ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken cancellationToken = default);
    IAsyncEnumerable<TransportFrame> ReadEventsAsync(CancellationToken cancellationToken = default);
}
public interface ITransportFactory { ITransport Create(TransportResource resource); }
public interface IProtocolCodec
{
    string ProtocolId { get; }
    ReadOnlyMemory<byte> Encode(string template, IReadOnlyDictionary<string, object?> values);
    IReadOnlyDictionary<string, object?> Decode(ReadOnlyMemory<byte> payload, string template);
}
public interface IDriverCatalog { IReadOnlyList<DriverDescriptor> GetAll(); DriverDescriptor? Find(string id); }
public interface IDeviceDriver : IAsyncDisposable
{
    string DeviceId { get; }
    string DriverId { get; }
    Task<ChannelReading> ReadAsync(ChannelDefinition channel, CancellationToken cancellationToken = default);
    Task<ChannelReading> WriteAsync(ChannelDefinition channel, object? value, CancellationToken cancellationToken = default);
}
public interface IDeviceDriverFactory { IDeviceDriver Create(DeviceDefinition device, TransportResource transport); }
public interface IChannelService
{
    event EventHandler<ChannelValueUpdatedEventArgs>? ValueUpdated;
    Task<ChannelReading> ReadAsync(string deviceId, string channelId, CancellationToken cancellationToken = default);
    Task<ChannelReading> WriteAsync(string deviceId, string channelId, object? value, CancellationToken cancellationToken = default);
    IReadOnlyDictionary<string, ChannelReading> Snapshot();
}
public interface IPollingService
{
    Task RunAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> StartAsync(string deviceId, CancellationToken cancellationToken = default);
    Task<bool> StopAsync(string deviceId);
    Task<bool> RestartAsync(string deviceId, CancellationToken cancellationToken = default);
    PollingStatus GetStatus(string deviceId);
}

public sealed class SimulatedTransportFactory : ITransportFactory
{
    public ITransport Create(TransportResource resource) => new MemoryTransport(resource.Id);
}

public sealed class PassthroughProtocolCodec : IProtocolCodec
{
    public PassthroughProtocolCodec(string protocolId) => ProtocolId = protocolId;
    public string ProtocolId { get; }
    public ReadOnlyMemory<byte> Encode(string template, IReadOnlyDictionary<string, object?> values)
    {
        var text = template;
        foreach (var item in values) text = text.Replace("{" + item.Key + "}", Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return System.Text.Encoding.ASCII.GetBytes(text);
    }
    public IReadOnlyDictionary<string, object?> Decode(ReadOnlyMemory<byte> payload, string template) =>
        new Dictionary<string, object?> { ["raw"] = System.Text.Encoding.ASCII.GetString(payload.Span) };
}

public sealed class BuiltInDriverCatalog : IDriverCatalog
{
    private static readonly IReadOnlyList<DriverDescriptor> Drivers =
    [
        new("simulated", "模拟设备驱动", "1.0", ["serial", "tcp", "can", "modbus_tcp", "modbus_rtu", "custom"], ["scpi", "dbc", "modbus", "custom"], "用于离线验证资源、通道读写和轮询逻辑的模拟驱动")
        ,new("itech.scpi", "艾德克斯公共 SCPI 驱动", "0.2", ["serial", "tcp", "visa"], ["scpi"], "公共 SCPI 电源驱动；型号命令通过 Profile 配置")
    ];
    public IReadOnlyList<DriverDescriptor> GetAll() => Drivers;
    public DriverDescriptor? Find(string id) => Drivers.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed class MemoryTransport : ITransport
{
    private readonly Channel<TransportFrame> _events = Channel.CreateUnbounded<TransportFrame>();
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<TransportFrame?>>? _handler;
    private int _disposed;
    public MemoryTransport(string id, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<TransportFrame?>>? handler = null) { Id = id; _handler = handler; }
    public string Id { get; }
    public TransportState State { get; private set; } = TransportState.Closed;
    public event EventHandler? StateChanged;
    public Task OpenAsync(CancellationToken cancellationToken = default) { ThrowIfDisposed(); cancellationToken.ThrowIfCancellationRequested(); State = TransportState.Open; StateChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public Task CloseAsync(CancellationToken cancellationToken = default) { if (_disposed == 0) { State = TransportState.Closed; StateChanged?.Invoke(this, EventArgs.Empty); } return Task.CompletedTask; }
    public async Task<TransportFrame?> TransactAsync(ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed(); if (State != TransportState.Open) throw new InvalidOperationException($"传输资源未打开: {Id}"); if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeoutSource.CancelAfter(timeout);
        try { return _handler is null ? null : await _handler(request, timeoutSource.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException($"传输请求超时: {Id}"); }
    }
    public async IAsyncEnumerable<TransportFrame> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await foreach (var frame in _events.Reader.ReadAllAsync(cancellationToken)) yield return frame; }
    public bool PublishEvent(TransportFrame frame) => _disposed == 0 && _events.Writer.TryWrite(frame);
    public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { State = TransportState.Closed; _events.Writer.TryComplete(); StateChanged?.Invoke(this, EventArgs.Empty); } return ValueTask.CompletedTask; }
    private void ThrowIfDisposed() { if (_disposed != 0) throw new ObjectDisposedException(nameof(MemoryTransport)); }
}

public sealed class SimulatedDeviceDriverFactory : IDeviceDriverFactory { public IDeviceDriver Create(DeviceDefinition device, TransportResource transport) => new SimulatedDeviceDriver(device); }
public sealed class SimulatedDeviceDriver : IDeviceDriver
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    public SimulatedDeviceDriver(DeviceDefinition device) { DeviceId = device.Id; DriverId = device.DriverId; foreach (var channel in device.Channels) _values[channel.Id] = null; }
    public string DeviceId { get; }
    public string DriverId { get; }
    public Task<ChannelReading> ReadAsync(ChannelDefinition channel, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (channel.Access == "write") throw new InvalidOperationException($"通道不可读: {channel.Id}"); _values.TryGetValue(channel.Id, out var value); return Task.FromResult(new ChannelReading(DeviceId, channel.Id, value, ChannelQuality.Good, DateTimeOffset.UtcNow)); }
    public Task<ChannelReading> WriteAsync(ChannelDefinition channel, object? value, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (channel.Access == "read") throw new InvalidOperationException($"通道不可写: {channel.Id}"); _values[channel.Id] = value; return Task.FromResult(new ChannelReading(DeviceId, channel.Id, value, ChannelQuality.Good, DateTimeOffset.UtcNow)); }
    public ValueTask DisposeAsync() { _values.Clear(); return ValueTask.CompletedTask; }
}

public sealed class SimulatedChannelService : IChannelService, IDisposable
{
    private readonly IResourceCatalogService _catalog; private readonly Hardware.IDeviceDriverFactory _drivers;
    private readonly Dictionary<string, Hardware.IDeviceDriver> _deviceDrivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ChannelReading> _readings = new(StringComparer.OrdinalIgnoreCase);
    public SimulatedChannelService(IResourceCatalogService catalog, Hardware.IDeviceDriverFactory drivers) { _catalog = catalog; _drivers = drivers; _catalog.Changed += CatalogChanged; Reload(); }
    public event EventHandler<ChannelValueUpdatedEventArgs>? ValueUpdated;
    public async Task<ChannelReading> ReadAsync(string deviceId, string channelId, CancellationToken cancellationToken = default)
    {
        var (_, channel, driver) = Resolve(deviceId, channelId); var key = $"{deviceId}/{channelId}";
        try { var value = await driver.ReadAsync(channel.Id, cancellationToken); var reading = WithHistory(key, new ChannelReading(deviceId, channelId, DriverValueConversion.FromJson(value), ChannelQuality.Good, DateTimeOffset.UtcNow)); Publish(reading); return reading; }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        { PublishFailure(deviceId, channelId, key, error); throw; }
    }
    public async Task<ChannelReading> WriteAsync(string deviceId, string channelId, object? value, CancellationToken cancellationToken = default)
    {
        var (_, channel, driver) = Resolve(deviceId, channelId); var key = $"{deviceId}/{channelId}";
        try { await driver.WriteAsync(channel.Id, DriverValueConversion.ToJson(value), cancellationToken); var reading = WithHistory(key, new ChannelReading(deviceId, channelId, value, ChannelQuality.Good, DateTimeOffset.UtcNow)); Publish(reading); return reading; }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        { PublishFailure(deviceId, channelId, key, error); throw; }
    }
    public IReadOnlyDictionary<string, ChannelReading> Snapshot() => new Dictionary<string, ChannelReading>(_readings, StringComparer.OrdinalIgnoreCase);
    public void Dispose() { _catalog.Changed -= CatalogChanged; foreach (var driver in _deviceDrivers.Values) driver.DisposeAsync().AsTask().GetAwaiter().GetResult(); _deviceDrivers.Clear(); }
    private void CatalogChanged(object? sender, EventArgs e) => Reload();
    private void Reload() { foreach (var driver in _deviceDrivers.Values) driver.DisposeAsync().AsTask().GetAwaiter().GetResult(); _deviceDrivers.Clear(); _readings.Clear(); var transports = _catalog.Catalog.Transports.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase); foreach (var device in _catalog.Catalog.Devices) if (transports.TryGetValue(device.TransportId, out var transport) && transport.Enabled) _deviceDrivers[device.Id] = _drivers.Create(device, transport); }
    private (DeviceDefinition Device, ChannelDefinition Channel, Hardware.IDeviceDriver Driver) Resolve(string deviceId, string channelId) { var device = _catalog.Catalog.Devices.FirstOrDefault(item => item.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"设备不存在: {deviceId}"); var channel = device.Channels.FirstOrDefault(item => item.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"通道不存在: {deviceId}/{channelId}"); if (!_deviceDrivers.TryGetValue(device.Id, out var driver)) throw new InvalidOperationException($"设备通信资源不可用: {deviceId}"); return (device, channel, driver); }
    private ChannelReading WithHistory(string key, ChannelReading reading)
    {
        _readings.TryGetValue(key, out var previous);
        return reading with { LastSuccessfulValue = reading.Quality == ChannelQuality.Good ? reading.Value : previous?.LastSuccessfulValue, LastSuccessfulTimestamp = reading.Quality == ChannelQuality.Good ? reading.Timestamp : previous?.LastSuccessfulTimestamp };
    }
    private void PublishFailure(string deviceId, string channelId, string key, Exception error)
    {
        _readings.TryGetValue(key, out var previous);
        var quality = error is TimeoutException or OperationCanceledException ? ChannelQuality.Timeout : ChannelQuality.Error;
        var reading = new ChannelReading(deviceId, channelId, previous?.Value, quality, DateTimeOffset.UtcNow, error.Message,
            previous?.LastSuccessfulValue ?? previous?.Value, previous?.LastSuccessfulTimestamp, false);
        Publish(reading);
    }
    private void Publish(ChannelReading reading) { _readings[$"{reading.DeviceId}/{reading.ChannelId}"] = reading; ValueUpdated?.Invoke(this, new(reading)); }
}

public sealed class PollingService : IPollingService, IDisposable
{
    private readonly IResourceCatalogService _catalog;
    private readonly IChannelService _channels;
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Source, PollingStatus Status)> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PollingStatus> _lastStatuses = new(StringComparer.OrdinalIgnoreCase);
    public PollingService(IResourceCatalogService catalog, IChannelService channels) { _catalog = catalog; _channels = channels; _catalog.Changed += CatalogChanged; }
    public async Task<bool> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_running.ContainsKey(deviceId)) return false;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channelsForDevice = FindPeriodic(deviceId).Length;
        var status = new PollingStatus(deviceId, true, channelsForDevice, DateTimeOffset.UtcNow);
        _lastStatuses[deviceId] = status;
        var task = RunTrackedAsync(deviceId, source, status);
        if (!_running.TryAdd(deviceId, (task, source, status))) { source.Dispose(); return false; }
        await Task.Yield(); return true;
    }
    public async Task<bool> StopAsync(string deviceId)
    {
        if (!_running.TryRemove(deviceId, out var running)) return false;
        running.Source.Cancel();
        try { await running.Task; } catch (OperationCanceledException) { }
        _lastStatuses[deviceId] = running.Status with { IsRunning = false };
        running.Source.Dispose(); return true;
    }
    public async Task<bool> RestartAsync(string deviceId, CancellationToken cancellationToken = default) { await StopAsync(deviceId); return await StartAsync(deviceId, cancellationToken); }
    public PollingStatus GetStatus(string deviceId) => _running.TryGetValue(deviceId, out var running) ? running.Status : _lastStatuses.TryGetValue(deviceId, out var status) ? status : new(deviceId, false, 0);
    public async Task RunAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var periodic = FindPeriodic(deviceId);
        await Task.WhenAll(periodic.Select(channel => PollChannelAsync(deviceId, channel, cancellationToken)));
    }
    private async Task RunTrackedAsync(string deviceId, CancellationTokenSource source, PollingStatus status)
    {
        try { await RunAsync(deviceId, source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) { _lastStatuses[deviceId] = status with { Error = error.Message, IsRunning = false }; }
        finally { _lastStatuses.TryAdd(deviceId, status with { IsRunning = false }); _running.TryRemove(deviceId, out _); }
    }
    private ChannelDefinition[] FindPeriodic(string deviceId)
    {
        var device = _catalog.Catalog.Devices.FirstOrDefault(item => item.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"设备不存在: {deviceId}");
        return device.Channels.Where(item => item.Polling.Mode == "periodic" && (item.Access is "read" or "read_write")).ToArray();
    }
    private async Task PollChannelAsync(string deviceId, ChannelDefinition channel, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0; ; attempt++)
            {
                try { using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(channel.Polling.TimeoutMs); await _channels.ReadAsync(deviceId, channel.Id, timeout.Token); break; }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < channel.Polling.RetryCount) { }
                catch (TimeoutException) when (attempt < channel.Polling.RetryCount) { }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                catch (Exception) when (attempt < channel.Polling.RetryCount) { }
                catch (Exception) { break; }
            }
            await Task.Delay(channel.Polling.IntervalMs, cancellationToken);
        }
    }
    private void CatalogChanged(object? sender, EventArgs args)
    {
        // A resource reload invalidates the driver/channel instances used by old loops.
        // Cancel them immediately; callers can start a new loop after the reload.
        foreach (var running in _running.Values) running.Source.Cancel();
    }
    public void Dispose()
    {
        _catalog.Changed -= CatalogChanged;
        foreach (var running in _running.Values) running.Source.Cancel();
        _running.Clear();
    }
}
