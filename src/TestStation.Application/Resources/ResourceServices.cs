using System.Text.Json;
using TestStation.Domain.Resources;

namespace TestStation.Application.Resources;

public sealed record ResourceCatalogSnapshot(ResourceCatalog Catalog, IReadOnlyList<string> Errors);

public interface IResourceCatalogRepository
{
    ResourceCatalogSnapshot Load(string root);
    void Save(string root, ResourceCatalog catalog);
}

public interface IResourceCatalogService
{
    event EventHandler? Changed;
    string? ProjectRoot { get; }
    ResourceCatalog Catalog { get; }
    IReadOnlyList<string> Errors { get; }
    void OpenProject(string root);
    void Save(ResourceCatalog catalog);
}

/// <summary>
/// Resource document editing boundary.  Views and view models use this service
/// for structural edits so that reference checks and validation stay out of UI.
/// </summary>
public interface IResourceCatalogEditingService
{
    TransportResource AddTransport(string type = "serial");
    DeviceDefinition AddDevice(string transportId, string driverId = "simulated");
    ChannelDefinition AddChannel(string deviceId);
    bool RemoveTransport(string transportId, out string error);
    bool RemoveDevice(string deviceId, out string error);
    bool RemoveChannel(string deviceId, string channelId, out string error);
    void UpdateTransport(string transportId, string name, string type, bool enabled);
    void UpdateDevice(string deviceId, string name, string driverId, string transportId, string protocolId);
    void UpdateChannel(string deviceId, string channelId, string name, string dataType, string unit, string access);
}

public sealed class ResourceCatalogEditingService(IResourceCatalogService catalogService) : IResourceCatalogEditingService
{
    private ResourceCatalog Catalog => catalogService.Catalog;

    public TransportResource AddTransport(string type = "serial")
    {
        var item = new TransportResource
        {
            Id = "transport_" + Guid.NewGuid().ToString("N")[..8],
            Name = "新通信资源",
            Type = type
        };
        Catalog.Transports.Add(item);
        return item;
    }

    public DeviceDefinition AddDevice(string transportId, string driverId = "simulated")
    {
        if (Catalog.Transports.All(item => !item.Id.Equals(transportId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"通信资源不存在: {transportId}");
        var item = new DeviceDefinition
        {
            Id = "device_" + Guid.NewGuid().ToString("N")[..8],
            Name = "新设备",
            DriverId = driverId,
            TransportId = transportId
        };
        Catalog.Devices.Add(item);
        return item;
    }

    public ChannelDefinition AddChannel(string deviceId)
    {
        var device = FindDevice(deviceId);
        var item = new ChannelDefinition
        {
            Id = "channel_" + Guid.NewGuid().ToString("N")[..8],
            Name = "新通道"
        };
        device.Channels.Add(item);
        return item;
    }

    public bool RemoveTransport(string transportId, out string error)
    {
        if (Catalog.Devices.Any(item => item.TransportId.Equals(transportId, StringComparison.OrdinalIgnoreCase)))
        {
            error = "该通信资源仍被设备使用，不能删除";
            return false;
        }
        var removed = Catalog.Transports.RemoveAll(item => item.Id.Equals(transportId, StringComparison.OrdinalIgnoreCase)) > 0;
        error = removed ? "" : "通信资源不存在";
        return removed;
    }

    public bool RemoveDevice(string deviceId, out string error)
    {
        var removed = Catalog.Devices.RemoveAll(item => item.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) > 0;
        error = removed ? "" : "设备不存在";
        return removed;
    }

    public bool RemoveChannel(string deviceId, string channelId, out string error)
    {
        var device = FindDevice(deviceId);
        var removed = device.Channels.RemoveAll(item => item.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase)) > 0;
        error = removed ? "" : "通道不存在";
        return removed;
    }

    public void UpdateTransport(string transportId, string name, string type, bool enabled)
    {
        var item = Catalog.Transports.FirstOrDefault(item => item.Id.Equals(transportId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeyNotFoundException($"通信资源不存在: {transportId}");
        item.Name = name.Trim();
        item.Type = type.Trim().ToLowerInvariant();
        item.Enabled = enabled;
    }

    public void UpdateDevice(string deviceId, string name, string driverId, string transportId, string protocolId)
    {
        var item = FindDevice(deviceId);
        if (Catalog.Transports.All(resource => !resource.Id.Equals(transportId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"通信资源不存在: {transportId}");
        item.Name = name.Trim(); item.DriverId = driverId.Trim(); item.TransportId = transportId.Trim(); item.ProtocolId = protocolId.Trim();
    }

    public void UpdateChannel(string deviceId, string channelId, string name, string dataType, string unit, string access)
    {
        var item = FindDevice(deviceId).Channels.FirstOrDefault(channel => channel.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new KeyNotFoundException($"通道不存在: {deviceId}/{channelId}");
        item.Name = name.Trim(); item.DataType = dataType.Trim(); item.Unit = unit.Trim(); item.Access = access.Trim().ToLowerInvariant();
    }

    private DeviceDefinition FindDevice(string deviceId) =>
        Catalog.Devices.FirstOrDefault(item => item.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"设备不存在: {deviceId}");
}

public sealed class ResourceCatalogService(IResourceCatalogRepository repository) : IResourceCatalogService
{
    public event EventHandler? Changed;
    public string? ProjectRoot { get; private set; }
    public ResourceCatalog Catalog { get; private set; } = new();
    public IReadOnlyList<string> Errors { get; private set; } = [];

    public void OpenProject(string root)
    {
        var snapshot = repository.Load(root);
        ProjectRoot = root;
        Catalog = snapshot.Catalog;
        Errors = snapshot.Errors;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save(ResourceCatalog catalog)
    {
        ResourceCatalogValidator.Validate(catalog);
        repository.Save(ProjectRoot ?? throw new InvalidOperationException("请先打开或创建工程"), catalog);
        Catalog = Clone(catalog);
        Errors = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static ResourceCatalog Clone(ResourceCatalog source) =>
        JsonSerializer.Deserialize<ResourceCatalog>(JsonSerializer.Serialize(source, JsonOptions), JsonOptions) ?? new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

public static class ResourceCatalogValidator
{
    private static readonly HashSet<string> TransportTypes = ["serial", "tcp", "can", "modbus_tcp", "modbus_rtu", "custom"];
    private static readonly HashSet<string> PollingModes = ["manual", "periodic", "event"];
    private static readonly HashSet<string> MappingProtocols = ["scpi", "dbc", "modbus", "custom"];
    private static readonly HashSet<string> ByteOrders = ["little_endian", "big_endian"];

    public static void Validate(ResourceCatalog catalog)
    {
        if (catalog is null || catalog.Version != 1) throw new InvalidDataException("资源目录版本不受支持");
        var transportIds = Unique(catalog.Transports.Select(item => item.Id), "通信资源 ID");
        Unique(catalog.Devices.Select(item => item.Id), "设备 ID");
        foreach (var transport in catalog.Transports)
        {
            Required(transport.Id, "通信资源 ID"); Required(transport.Name, "通信资源名称");
            if (!TransportTypes.Contains(transport.Type)) throw new InvalidDataException($"不支持的通信资源类型: {transport.Type}");
        }
        foreach (var device in catalog.Devices)
        {
            Required(device.Id, "设备 ID"); Required(device.Name, "设备名称"); Required(device.DriverId, "驱动 ID"); Required(device.TransportId, "设备通信资源");
            if (!transportIds.Contains(device.TransportId)) throw new InvalidDataException($"设备 {device.Id} 引用了不存在的通信资源");
            var channelIds = Unique(device.Channels.Select(item => item.Id), $"设备 {device.Id} 的通道 ID");
            foreach (var channel in device.Channels)
            {
                Required(channel.Id, "通道 ID"); Required(channel.Name, "通道名称");
                if (channel.Access is not ("read" or "write" or "read_write")) throw new InvalidDataException($"通道 {channel.Id} 的访问方式无效");
                if (!MappingProtocols.Contains(channel.Mapping.ProtocolKind)) throw new InvalidDataException($"通道 {channel.Id} 的协议映射类型无效");
                if (!ByteOrders.Contains(channel.Mapping.ByteOrder)) throw new InvalidDataException($"通道 {channel.Id} 的字节序无效");
                if (double.IsNaN(channel.Mapping.Scale) || double.IsInfinity(channel.Mapping.Scale) || double.IsNaN(channel.Mapping.Offset) || double.IsInfinity(channel.Mapping.Offset)) throw new InvalidDataException($"通道 {channel.Id} 的缩放参数无效");
                if (channel.Mapping.RegisterAddress is < 0) throw new InvalidDataException($"通道 {channel.Id} 的寄存器地址无效");
                if (!PollingModes.Contains(channel.Polling.Mode)) throw new InvalidDataException($"通道 {channel.Id} 的轮询模式无效");
                if (channel.Polling.IntervalMs < 1 || channel.Polling.TimeoutMs < 1 || channel.Polling.RetryCount < 0) throw new InvalidDataException($"通道 {channel.Id} 的轮询参数无效");
                if (channel.Polling.Mode == "event" && string.IsNullOrWhiteSpace(channel.Polling.EventKey)) throw new InvalidDataException($"事件通道 {channel.Id} 缺少事件标识");
                _ = channelIds;
            }
        }
    }

    private static HashSet<string> Unique(IEnumerable<string> values, string label)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            Required(value, label);
            if (!result.Add(value)) throw new InvalidDataException($"{label}重复: {value}");
        }
        return result;
    }

    private static void Required(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"{label}无效");
    }
}
