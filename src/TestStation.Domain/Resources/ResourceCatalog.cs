using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestStation.Domain.Resources;

public sealed class ResourceCatalog
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("transports")] public List<TransportResource> Transports { get; set; } = [];
    [JsonPropertyName("devices")] public List<DeviceDefinition> Devices { get; set; } = [];
}

public sealed class TransportResource
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "serial";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("settings")] public Dictionary<string, JsonElement> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DeviceDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("driver_id")] public string DriverId { get; set; } = "";
    [JsonPropertyName("transport_id")] public string TransportId { get; set; } = "";
    [JsonPropertyName("protocol_id")] public string ProtocolId { get; set; } = "";
    [JsonPropertyName("channels")] public List<ChannelDefinition> Channels { get; set; } = [];
    [JsonPropertyName("metadata")] public Dictionary<string, JsonElement> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ChannelDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("data_type")] public string DataType { get; set; } = "number";
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("access")] public string Access { get; set; } = "read_write";
    [JsonPropertyName("mapping")] public ChannelMapping Mapping { get; set; } = new();
    [JsonPropertyName("polling")] public PollingPolicy Polling { get; set; } = new();
}

public sealed class ChannelMapping
{
    /// <summary>统一映射类型：scpi、dbc、modbus 或 custom。</summary>
    [JsonPropertyName("protocol_kind")] public string ProtocolKind { get; set; } = "custom";
    [JsonPropertyName("read_frame_id")] public string ReadFrameId { get; set; } = "";
    [JsonPropertyName("read_field_id")] public string ReadFieldId { get; set; } = "";
    [JsonPropertyName("write_frame_id")] public string WriteFrameId { get; set; } = "";
    [JsonPropertyName("write_field_id")] public string WriteFieldId { get; set; } = "";
    [JsonPropertyName("message_id")] public string MessageId { get; set; } = "";
    [JsonPropertyName("signal_name")] public string SignalName { get; set; } = "";
    [JsonPropertyName("register_address")] public int? RegisterAddress { get; set; }
    [JsonPropertyName("byte_order")] public string ByteOrder { get; set; } = "little_endian";
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("query_template")] public string QueryTemplate { get; set; } = "";
    [JsonPropertyName("command_template")] public string CommandTemplate { get; set; } = "";
}

public sealed class PollingPolicy
{
    [JsonPropertyName("mode")] public string Mode { get; set; } = "manual";
    [JsonPropertyName("interval_ms")] public int IntervalMs { get; set; } = 1000;
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; } = 500;
    [JsonPropertyName("retry_count")] public int RetryCount { get; set; }
    [JsonPropertyName("event_key")] public string EventKey { get; set; } = "";
}
