using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.Application.Drivers;

/// <summary>Routes the legacy transport contract through the canonical session factory.</summary>
public sealed class LegacyTransportFactoryAdapter : ITransportFactory
{
    private readonly Hardware.ITransportSessionFactory _sessions;

    public LegacyTransportFactoryAdapter(Hardware.ITransportSessionFactory sessions) => _sessions = sessions;

    public ITransport Create(TransportResource resource) => new LegacyTransportAdapter(resource, _sessions);
}

internal sealed class LegacyTransportAdapter : ITransport
{
    private readonly TransportResource _resource;
    private readonly Hardware.ITransportSessionFactory _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Hardware.ITransportSession? _session;
    private bool _disposed;

    public LegacyTransportAdapter(TransportResource resource, Hardware.ITransportSessionFactory factory)
    {
        _resource = resource;
        _factory = factory;
    }

    public string Id => _resource.Id;
    public TransportState State => _session?.State ?? TransportState.Closed;
    public event EventHandler? StateChanged;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is not null) return;
            _session = _factory.Create(_resource);
            _session.StateChanged += SessionStateChanged;
            await _session.OpenAsync(new(_resource.Id, TransportSettingsAccessor.NormalizeType(_resource.Type), _resource.Settings,
                _resource.LeaseMode.Equals("shared_bus", StringComparison.OrdinalIgnoreCase)
                    ? Hardware.ResourceLeaseMode.SharedBus : Hardware.ResourceLeaseMode.Exclusive), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (_session is not null)
            {
                _session.StateChanged -= SessionStateChanged;
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = _session;
            _session = null;
            if (session is not null)
            {
                session.StateChanged -= SessionStateChanged;
                try { await session.CloseAsync(cancellationToken).ConfigureAwait(false); }
                finally { await session.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<TransportFrame?> TransactAsync(ReadOnlyMemory<byte> request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var byteSession = _session as Hardware.IByteTransportSession ?? throw new NotSupportedException($"传输资源不支持字节请求响应：{_resource.Type}");
            var payload = await byteSession.ExchangeAsync(request, true, timeout, cancellationToken).ConfigureAwait(false);
            return payload is null ? null : new TransportFrame(DateTimeOffset.UtcNow, payload.Value.ToArray());
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<TransportFrame> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var can = _session as Hardware.ICanBusSession;
        if (can is null) yield break;
        await foreach (var frame in can.ReceiveAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            yield return new TransportFrame(frame.Timestamp ?? DateTimeOffset.UtcNow, frame.Data.ToArray(), $"0x{frame.Id:X}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_session is not null)
            {
                _session.StateChanged -= SessionStateChanged;
                await _session.DisposeAsync().ConfigureAwait(false);
                _session = null;
            }
        }
        finally { _gate.Release(); }
    }

    private void SessionStateChanged(object? sender, EventArgs e) => StateChanged?.Invoke(this, e);
}

/// <summary>
/// Compatibility boundary for drivers written against the original channel
/// contract.  The rest of the application consumes Hardware.IDeviceDriver;
/// legacy implementations remain behind this adapter until they are migrated.
/// </summary>
public sealed class LegacyDeviceDriverFactoryAdapter : Hardware.IDeviceDriverFactory
{
    private readonly IDeviceDriverFactory _legacy;

    public LegacyDeviceDriverFactoryAdapter(IDeviceDriverFactory legacy) => _legacy = legacy;

    public Hardware.IDeviceDriver Create(DeviceDefinition device, TransportResource resource) =>
        new LegacyDeviceDriverAdapter(_legacy.Create(device, resource), device);
}

internal sealed class LegacyDeviceDriverAdapter : Hardware.IDeviceDriver
{
    private readonly IDeviceDriver _legacy;
    private readonly DeviceDefinition _device;
    private bool _disposed;

    public LegacyDeviceDriverAdapter(IDeviceDriver legacy, DeviceDefinition device)
    {
        _legacy = legacy;
        _device = device;
    }

    public string DeviceId => _device.Id;
    public string FamilyId => _device.FamilyId;
    public string ModelId => _device.ModelId;
    public IReadOnlyList<SequenceChannelDefinition> Channels => _device.Channels
        .Select(channel => new SequenceChannelDefinition(channel.Id, channel.DataType, channel.Name,
            channel.Access is "read" or "read_write", channel.Access is "write" or "read_write"))
        .ToArray();

    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var channel = Resolve(channelId);
        var reading = await _legacy.ReadAsync(channel, cancellationToken).ConfigureAwait(false);
        return DriverValueConversion.ToJson(reading.Value);
    }

    public async Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default)
    {
        EnsureNotDisposed();
        var channel = Resolve(channelId);
        await _legacy.WriteAsync(channel, DriverValueConversion.FromJson(value), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _legacy.DisposeAsync().ConfigureAwait(false);
    }

    private ChannelDefinition Resolve(string channelId) => _device.Channels.FirstOrDefault(channel =>
        channel.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase) ||
        channelId.EndsWith("." + channel.Id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"设备通道不存在：{_device.Id}.{channelId}");

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal static class DriverValueConversion
{
    public static JsonElement ToJson(object? value) => value switch
    {
        null => JsonSerializer.SerializeToElement<object?>(null),
        JsonElement element => element.Clone(),
        _ => JsonSerializer.SerializeToElement(value)
    };

    public static object? FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when value.TryGetDouble(out var number) => number,
        _ => value.Clone()
    };
}
