using System.Text.Json;
using TestStation.Application;
using TestStation.Domain.Resources;

namespace TestStation.Application.Hardware;

public enum ResourceLeaseMode { Exclusive, SharedBus }

public sealed record TransportEndpoint(
    string ResourceId,
    string Type,
    IReadOnlyDictionary<string, JsonElement> Settings,
    ResourceLeaseMode LeaseMode = ResourceLeaseMode.Exclusive);

public interface ITransportSession : IAsyncDisposable
{
    string ResourceId { get; }
    string Type { get; }
    Drivers.TransportState State { get; }
    event EventHandler? StateChanged;
    Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>Byte-stream transport used by text and request/response protocols.</summary>
public interface IByteTransportSession : ITransportSession
{
    Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expectResponse,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed record CanFrame(
    uint Id,
    ReadOnlyMemory<byte> Data,
    bool IsExtended = false,
    bool IsRemote = false,
    bool IsFd = false,
    bool BitRateSwitch = false,
    DateTimeOffset? Timestamp = null,
    byte RemoteDataLength = 0)
{
    public void Validate()
    {
        var maxId = IsExtended ? 0x1FFFFFFFu : 0x7FFu;
        if (Id > maxId) throw new ArgumentOutOfRangeException(nameof(Id), "CAN 标识符超出标准帧或扩展帧范围");
        if (Data.Length > (IsFd ? 64 : 8)) throw new ArgumentException("CAN 数据长度超出帧类型限制", nameof(Data));
        if (!IsFd && BitRateSwitch) throw new ArgumentException("经典 CAN 帧不能设置 BRS", nameof(BitRateSwitch));
        if (IsFd && IsRemote) throw new ArgumentException("CAN FD 不支持远程帧", nameof(IsRemote));
        if (IsRemote && Data.Length != 0) throw new ArgumentException("远程帧不能包含数据", nameof(Data));
        if (!IsRemote && RemoteDataLength != 0) throw new ArgumentException("普通帧不能设置远程帧长度", nameof(RemoteDataLength));
        if (IsRemote && RemoteDataLength > 8) throw new ArgumentOutOfRangeException(nameof(RemoteDataLength), "经典 CAN 远程帧长度必须在 0～8 之间");
        if (IsFd && Data.Length is not (0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 12 or 16 or 20 or 24 or 32 or 48 or 64))
            throw new ArgumentException("CAN FD 数据长度必须是合法的 DLC 长度", nameof(Data));
    }
}

public sealed record CanFrameFilter(uint? Id = null, uint? Mask = null, bool? IsExtended = null, bool? IsFd = null, bool? IsRemote = null, byte[]? DataPrefix = null)
{
    public bool Matches(CanFrame frame)
    {
        if (IsExtended.HasValue && IsExtended.Value != frame.IsExtended) return false;
        if (IsFd.HasValue && IsFd.Value != frame.IsFd) return false;
        if (IsRemote.HasValue && IsRemote.Value != frame.IsRemote) return false;
        if (DataPrefix is not null && (frame.Data.Length < DataPrefix.Length || !frame.Data.Span[..DataPrefix.Length].SequenceEqual(DataPrefix))) return false;
        return !Id.HasValue || !Mask.HasValue ? !Id.HasValue || frame.Id == Id.Value : (frame.Id & Mask.Value) == (Id.Value & Mask.Value);
    }
}

/// <summary>Frame-oriented bus transport. CAN is not forced into byte request/response semantics.</summary>
public interface ICanBusSession : ITransportSession
{
    Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, CancellationToken cancellationToken = default);
    Task<CanFrame> SendAndReceiveAsync(CanFrame frame, CanFrameFilter expectedFilter, TimeSpan timeout,
        CancellationToken cancellationToken = default);
    long DroppedFrameCount { get; }
}

public interface ITransportSessionFactory
{
    ITransportSession Create(TransportResource resource);
    IByteTransportSession CreateByte(TransportResource resource) => Create(resource) as IByteTransportSession
        ?? throw new NotSupportedException($"通信资源不支持字节流传输：{resource.Type}");
    ICanBusSession CreateCan(TransportResource resource) => Create(resource) as ICanBusSession
        ?? throw new NotSupportedException($"通信资源不支持 CAN 总线传输：{resource.Type}");
}

public interface IProtocolSession : IAsyncDisposable
{
    string ProtocolId { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task SendAsync(string command, CancellationToken cancellationToken = default);
    Task<string> QueryAsync(string command, CancellationToken cancellationToken = default);
}

/// <summary>Protocol contract for binary request/response protocols such as Modbus RTU/TCP.</summary>
public interface IByteProtocolSession : IAsyncDisposable
{
    string ProtocolId { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>> ExecuteAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken = default);
}

/// <summary>Protocol contract for frame-oriented bus protocols such as CANopen and J1939.</summary>
public interface ICanProtocolSession : IAsyncDisposable
{
    string ProtocolId { get; }
    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync(CancellationToken cancellationToken = default);
    Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default);
    IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, CancellationToken cancellationToken = default);
}

public interface IDeviceDriver : IAsyncDisposable
{
    string DeviceId { get; }
    string FamilyId { get; }
    string ModelId { get; }
    IReadOnlyList<SequenceChannelDefinition> Channels { get; }
    Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default);
    Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default);
}

public interface IDeviceDriverFactory
{
    IDeviceDriver Create(DeviceDefinition device, TransportResource resource);
}

public interface IDeviceSession : IAsyncDisposable
{
    string DeviceId { get; }
    string ResourceId { get; }
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default);
    Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default);
}

public interface IResourceManager
{
    IReadOnlyList<TransportResource> GetResources();
    TransportResource? Find(string resourceId);
    IDisposable Acquire(string resourceId, string owner, ResourceLeaseMode? mode = null);
    bool IsLeased(string resourceId);
    event EventHandler? Changed;
}

public sealed class ResourceLeaseException(string message) : InvalidOperationException(message);
