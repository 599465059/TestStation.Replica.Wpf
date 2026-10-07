using System.Globalization;
using System.Text.Json;
using TestStation.Domain.Resources;

namespace TestStation.Application.Resources;

/// <summary>Strongly typed view of transport settings kept in the project JSON.</summary>
public sealed record TransportSettings(
    string? Port = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? Host = null,
    int? TcpPort = null,
    int? ConnectTimeoutMs = null,
    string? CanChannel = null,
    int? CanBitrate = null,
    bool CanFdEnabled = false,
    int? UnitId = null);

public static class TransportSettingsAccessor
{
    public static IReadOnlyList<(string Key, string Label)> Schema(string type) => type.ToLowerInvariant() switch
    {
        "serial" => [("port", "串口"), ("baud_rate", "波特率"), ("data_bits", "数据位"), ("parity", "校验位"), ("stop_bits", "停止位")],
        "tcp" => [("host", "主机地址"), ("tcp_port", "端口"), ("connect_timeout_ms", "连接超时(ms)")],
        "can" => [("channel", "CAN 通道"), ("bitrate", "仲裁波特率"), ("fd_enabled", "CAN FD")],
        "modbus_tcp" => [("host", "主机地址"), ("tcp_port", "端口"), ("connect_timeout_ms", "连接超时(ms)"), ("unit_id", "站号")],
        "modbus_rtu" => [("port", "串口"), ("baud_rate", "波特率"), ("data_bits", "数据位"), ("parity", "校验位"), ("stop_bits", "停止位"), ("unit_id", "站号")],
        _ => []
    };

    public static string ReadValue(TransportResource resource, string key)
    {
        return resource.Settings.TryGetValue(key, out var value) ? value.ToString() : key switch
        {
            "baud_rate" => "115200",
            "data_bits" => "8",
            "parity" => "None",
            "stop_bits" => "1",
            "connect_timeout_ms" => "1000",
            "fd_enabled" => "false",
            _ => ""
        };
    }

    public static bool TryApply(TransportResource resource, IReadOnlyDictionary<string, string> values, out string error)
    {
        foreach (var (key, value) in values)
        {
            if (key is ("baud_rate" or "data_bits" or "tcp_port" or "connect_timeout_ms" or "bitrate" or "unit_id") &&
                (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number <= 0))
            { error = $"通信参数 {key} 必须是正整数"; return false; }
            if (key == "fd_enabled" && !bool.TryParse(value, out _))
            { error = "CAN FD 必须填写 true 或 false"; return false; }
        }
        foreach (var (key, value) in values)
        {
            if (key is ("baud_rate" or "data_bits" or "tcp_port" or "connect_timeout_ms" or "bitrate" or "unit_id"))
                resource.Settings[key] = JsonSerializer.SerializeToElement(int.Parse(value, CultureInfo.InvariantCulture));
            else if (key == "fd_enabled") resource.Settings[key] = JsonSerializer.SerializeToElement(bool.Parse(value));
            else resource.Settings[key] = JsonSerializer.SerializeToElement(value);
        }
        error = ""; return true;
    }

    public static TransportSettings Read(TransportResource resource)
    {
        var values = resource.Settings;
        return new(
            String(values, "port"), Int(values, "baud_rate"), Int(values, "data_bits"), String(values, "parity"), String(values, "stop_bits"),
            String(values, "host"), Int(values, "tcp_port", "port"), Int(values, "connect_timeout_ms"), String(values, "channel"),
            Int(values, "bitrate"), Bool(values, "fd_enabled"), Int(values, "unit_id"));
    }

    public static void Write(TransportResource resource, TransportSettings settings)
    {
        Set(resource.Settings, "port", settings.Port);
        Set(resource.Settings, "baud_rate", settings.BaudRate);
        Set(resource.Settings, "data_bits", settings.DataBits);
        Set(resource.Settings, "parity", settings.Parity);
        Set(resource.Settings, "stop_bits", settings.StopBits);
        Set(resource.Settings, "host", settings.Host);
        Set(resource.Settings, "tcp_port", settings.TcpPort);
        Set(resource.Settings, "connect_timeout_ms", settings.ConnectTimeoutMs);
        Set(resource.Settings, "channel", settings.CanChannel);
        Set(resource.Settings, "bitrate", settings.CanBitrate);
        Set(resource.Settings, "fd_enabled", settings.CanFdEnabled);
        Set(resource.Settings, "unit_id", settings.UnitId);
    }

    public static IReadOnlyDictionary<string, string> Describe(string type) => type.ToLowerInvariant() switch
    {
        "serial" or "modbus_rtu" => new Dictionary<string, string> { ["port"] = "串口名称", ["baud_rate"] = "波特率", ["data_bits"] = "数据位", ["parity"] = "校验位", ["stop_bits"] = "停止位", ["unit_id"] = "站号" },
        "tcp" or "modbus_tcp" => new Dictionary<string, string> { ["host"] = "主机地址", ["tcp_port"] = "端口", ["connect_timeout_ms"] = "连接超时", ["unit_id"] = "站号" },
        "can" => new Dictionary<string, string> { ["channel"] = "CAN 通道", ["bitrate"] = "仲裁波特率", ["fd_enabled"] = "CAN FD" },
        _ => new Dictionary<string, string>()
    };

    private static string? String(IReadOnlyDictionary<string, JsonElement> values, params string[] keys)
    {
        foreach (var key in keys) if (values.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null) return value.ToString();
        return null;
    }
    private static int? Int(IReadOnlyDictionary<string, JsonElement> values, params string[] keys)
    {
        var value = String(values, keys); return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }
    private static bool Bool(IReadOnlyDictionary<string, JsonElement> values, string key)
    {
        if (!values.TryGetValue(key, out var value)) return false;
        return value.ValueKind == JsonValueKind.True || bool.TryParse(value.ToString(), out var parsed) && parsed;
    }
    private static void Set(IDictionary<string, JsonElement> values, string key, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) values.Remove(key);
        else values[key] = JsonSerializer.SerializeToElement(value);
    }
}
