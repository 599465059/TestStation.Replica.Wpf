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
    int? UnitId = null,
    string? SerialMode = null,
    string? FlowControl = null,
    bool HalfDuplex = false,
    int DirectionDelayMs = 0,
    int? CanDataBitrate = null,
    bool CanListenOnly = false,
    string? CanBackendId = null,
    string? VisaResource = null,
    string? VisaBackend = null,
    int? MaxResponseBytes = null);

public sealed record TcpTransportSettings(string Host = "127.0.0.1", int Port = 5025, int ConnectTimeoutMs = 1500, int TimeoutMs = 1500, int MaxResponseBytes = 1024 * 1024, string Terminator = "\n");
public sealed record SerialTransportSettings(string Port = "COM1", int BaudRate = 115200, int DataBits = 8, string Parity = "None", string StopBits = "1", string Mode = "RS232", string FlowControl = "None", bool HalfDuplex = false, int DirectionDelayMs = 0, int TimeoutMs = 1500, int MaxResponseBytes = 1024 * 1024, string Terminator = "\n");
public sealed record CanTransportSettings(string Channel = "", int NominalBitRate = 500000, int? DataBitRate = null, bool CanFdEnabled = false, bool ListenOnly = false, string BackendId = "simulated");
public sealed record VisaTransportSettings(string ResourceName = "", int TimeoutMs = 1500, int MaxResponseBytes = 1024 * 1024, string Terminator = "\n", string BackendId = "");

public static class TransportSettingsAccessor
{
    public static void ValidateEndpoint(TransportResource resource)
    {
        var settings = Read(resource);
        if (string.IsNullOrWhiteSpace(resource.Id) || string.IsNullOrWhiteSpace(resource.Name)) throw new InvalidDataException("资源标识和名称不能为空");
        var type = NormalizeType(resource.Type);
        if (type is "tcp" or "udp")
        {
            if (string.IsNullOrWhiteSpace(settings.Host) || settings.TcpPort is not (>= 1 and <= 65535)) throw new InvalidDataException("TCP 地址或端口无效");
        }
        else if (type == "serial")
        {
            if (string.IsNullOrWhiteSpace(settings.Port) || settings.BaudRate is not (> 0) || settings.DataBits is not (>= 5 and <= 8) ||
                settings.Parity is not ("None" or "Odd" or "Even" or "Mark" or "Space") || settings.StopBits is not ("1" or "2" or "1.5" or "One" or "Two" or "OnePointFive"))
                throw new InvalidDataException("串口名称、波特率、数据位、校验位或停止位无效");
        }
        else if (type == "can")
        {
            if (string.IsNullOrWhiteSpace(settings.CanChannel)) throw new InvalidDataException("CAN 通道不能为空");
            if (settings.CanBitrate is not (> 0 and <= 8000000)) throw new InvalidDataException("CAN 仲裁波特率无效");
            if (settings.CanFdEnabled && settings.CanDataBitrate is not (> 0 and <= 8000000)) throw new InvalidDataException("CAN FD 数据波特率无效");
            if (resource.Settings.TryGetValue("can_backend_id", out var backend) && backend.ToString() is not ("" or "simulated" or "pcan" or "peak" or "zlg" or "kvaser" or "vector" or "tosun" or "ni-xnet" or "etas"))
                throw new InvalidDataException("CAN 后端必须是 simulated、pcan、zlg、kvaser、vector、tosun、ni-xnet 或 etas");
        }
        else if (type == "visa")
        {
            if (string.IsNullOrWhiteSpace(settings.VisaResource)) throw new InvalidDataException("VISA 资源名不能为空");
        }
        else throw new NotSupportedException($"不支持的通信资源类型：{resource.Type}");
        if (type == "serial" && settings.SerialMode is not ("RS232" or "RS422" or "RS485")) throw new InvalidDataException("串口模式必须是 RS232、RS422 或 RS485");
        if (type == "serial" && settings.FlowControl is not ("None" or "RtsCts" or "XOnXOff")) throw new InvalidDataException("串口流控无效");
        if (settings.DirectionDelayMs < 0 || settings.DirectionDelayMs > 60000) throw new InvalidDataException("串口方向切换延时无效");
        foreach (var key in new[] { "timeout_ms", "connect_timeout_ms" })
            if (resource.Settings.TryGetValue(key, out var timeout) && (!int.TryParse(timeout.ToString(), out var ms) || ms < 1 || ms > 60000)) throw new InvalidDataException("通信超时必须在 1～60000 ms 之间");
        if (resource.Settings.TryGetValue("max_response_bytes", out var maxResponse) && (!int.TryParse(maxResponse.ToString(), out var maxBytes) || maxBytes < 1 || maxBytes > 16 * 1024 * 1024)) throw new InvalidDataException("最大响应字节数必须在 1～16777216 之间");
        if (resource.Settings.TryGetValue("terminator", out var terminator) && (terminator.ValueKind != JsonValueKind.String || terminator.GetString() is not ("\n" or "\r\n" or "\r"))) throw new InvalidDataException("终止符无效");
    }
    public static IReadOnlyList<(string Key, string Label)> Schema(string type) => type.ToLowerInvariant() switch
    {
        "serial" => [("mode", "串口模式"), ("port", "串口"), ("baud_rate", "波特率"), ("data_bits", "数据位"), ("parity", "校验位"), ("stop_bits", "停止位"), ("flow_control", "流控"), ("half_duplex", "半双工"), ("direction_delay_ms", "方向切换延时(ms)"), ("timeout_ms", "超时(ms)"), ("max_response_bytes", "最大响应字节数"), ("terminator", "终止符")],
        "tcp" => [("host", "主机地址"), ("tcp_port", "端口"), ("connect_timeout_ms", "连接超时(ms)"), ("timeout_ms", "读写超时(ms)"), ("max_response_bytes", "最大响应字节数"), ("terminator", "终止符")],
        "udp" => [("host", "主机地址"), ("tcp_port", "UDP 端口"), ("connect_timeout_ms", "超时(ms)"), ("timeout_ms", "读写超时(ms)"), ("max_response_bytes", "最大响应字节数")],
        "visa" => [("visa_resource", "VISA 资源名"), ("backend_id", "VISA 后端"), ("timeout_ms", "超时(ms)"), ("max_response_bytes", "最大响应字节数"), ("terminator", "终止符")],
        "can" => [("can_backend_id", "CAN 后端"), ("channel", "CAN 通道"), ("bitrate", "仲裁波特率"), ("data_bitrate", "数据波特率"), ("fd_enabled", "CAN FD"), ("listen_only", "只听模式")],
        "modbus_tcp" => [("host", "主机地址"), ("tcp_port", "端口"), ("connect_timeout_ms", "连接超时(ms)"), ("timeout_ms", "读写超时(ms)"), ("max_response_bytes", "最大响应字节数"), ("unit_id", "站号")],
        "modbus_rtu" => [("port", "串口"), ("baud_rate", "波特率"), ("data_bits", "数据位"), ("parity", "校验位"), ("stop_bits", "停止位"), ("timeout_ms", "超时(ms)"), ("max_response_bytes", "最大响应字节数"), ("unit_id", "站号")],
        _ => []
    };

    public static string ReadValue(TransportResource resource, string key)
    {
        if (resource.Settings.TryGetValue(key, out var existing))
        {
            var text = existing.ToString();
            return key == "terminator" ? text switch { "\r\n" => "\\r\\n", "\r" => "\\r", "\n" => "\\n", _ => text } : text;
        }
        return key switch
        {
            "baud_rate" => "115200",
            "data_bits" => "8",
            "parity" => "None",
            "stop_bits" => "1",
            "connect_timeout_ms" => "1000",
            "fd_enabled" => "false",
            "listen_only" => "false",
            "mode" => "RS232",
            "flow_control" => "None",
            "half_duplex" => "false",
            "direction_delay_ms" => "0",
            "data_bitrate" => "2000000",
            "timeout_ms" => "1500",
            "max_response_bytes" => "1048576",
            "terminator" => "\\n",
            "can_backend_id" => "simulated",
            _ => ""
        };
    }

    public static bool TryApply(TransportResource resource, IReadOnlyDictionary<string, string> values, out string error)
    {
        foreach (var (key, value) in values)
        {
            if (key is ("baud_rate" or "data_bits" or "tcp_port" or "connect_timeout_ms" or "bitrate" or "data_bitrate" or "unit_id" or "timeout_ms" or "direction_delay_ms" or "max_response_bytes") &&
                (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || (key == "direction_delay_ms" ? number < 0 : number <= 0)))
            { error = $"通信参数 {key} 必须是正整数"; return false; }
            if (key is ("fd_enabled" or "listen_only" or "half_duplex") && !bool.TryParse(value, out _))
            { error = $"{key} 必须填写 true 或 false"; return false; }
            if (key == "mode" && value is not ("RS232" or "RS422" or "RS485"))
            { error = "串口模式必须是 RS232、RS422 或 RS485"; return false; }
            if (key == "flow_control" && value is not ("None" or "RtsCts" or "XOnXOff"))
            { error = "串口流控无效"; return false; }
            if (key == "terminator" && value is not ("\\n" or "\\r\\n" or "\\r"))
            { error = "终止符无效"; return false; }
        }
        foreach (var (key, value) in values)
        {
            if (key is ("baud_rate" or "data_bits" or "tcp_port" or "connect_timeout_ms" or "bitrate" or "data_bitrate" or "unit_id" or "timeout_ms" or "direction_delay_ms" or "max_response_bytes"))
                resource.Settings[key] = JsonSerializer.SerializeToElement(int.Parse(value, CultureInfo.InvariantCulture));
            else if (key is ("fd_enabled" or "listen_only" or "half_duplex")) resource.Settings[key] = JsonSerializer.SerializeToElement(bool.Parse(value));
            else if (key == "terminator") resource.Settings[key] = JsonSerializer.SerializeToElement(value switch { "\\n" => "\n", "\\r\\n" => "\r\n", "\\r" => "\r", _ => value });
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
            Int(values, "bitrate"), Bool(values, "fd_enabled"), Int(values, "unit_id"), String(values, "mode") ?? "RS232", String(values, "flow_control") ?? "None", Bool(values, "half_duplex"), Int(values, "direction_delay_ms") ?? 0, Int(values, "data_bitrate"), Bool(values, "listen_only"), String(values, "can_backend_id"), String(values, "visa_resource"), String(values, "backend_id"), Int(values, "max_response_bytes"));
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
        Set(resource.Settings, "can_backend_id", settings.CanBackendId);
        Set(resource.Settings, "bitrate", settings.CanBitrate);
        Set(resource.Settings, "fd_enabled", settings.CanFdEnabled);
        Set(resource.Settings, "unit_id", settings.UnitId);
        Set(resource.Settings, "mode", settings.SerialMode);
        Set(resource.Settings, "flow_control", settings.FlowControl);
        Set(resource.Settings, "half_duplex", settings.HalfDuplex);
        Set(resource.Settings, "direction_delay_ms", settings.DirectionDelayMs);
        Set(resource.Settings, "data_bitrate", settings.CanDataBitrate);
        Set(resource.Settings, "listen_only", settings.CanListenOnly);
        Set(resource.Settings, "visa_resource", settings.VisaResource);
        Set(resource.Settings, "backend_id", settings.VisaBackend);
        Set(resource.Settings, "max_response_bytes", settings.MaxResponseBytes);
        if (!resource.Settings.ContainsKey("terminator")) resource.Settings["terminator"] = JsonSerializer.SerializeToElement("\n");
    }

    public static IReadOnlyDictionary<string, string> Describe(string type) => type.ToLowerInvariant() switch
    {
        "serial" or "modbus_rtu" => new Dictionary<string, string> { ["mode"] = "串口模式", ["port"] = "串口名称", ["baud_rate"] = "波特率", ["data_bits"] = "数据位", ["parity"] = "校验位", ["stop_bits"] = "停止位", ["flow_control"] = "流控", ["half_duplex"] = "半双工", ["direction_delay_ms"] = "方向切换延时", ["timeout_ms"] = "超时", ["max_response_bytes"] = "最大响应字节数", ["terminator"] = "终止符", ["unit_id"] = "站号" },
        "tcp" or "modbus_tcp" => new Dictionary<string, string> { ["host"] = "主机地址", ["tcp_port"] = "端口", ["connect_timeout_ms"] = "连接超时", ["timeout_ms"] = "读写超时", ["max_response_bytes"] = "最大响应字节数", ["terminator"] = "终止符", ["unit_id"] = "站号" },
        "udp" => new Dictionary<string, string> { ["host"] = "主机地址", ["tcp_port"] = "UDP 端口", ["timeout_ms"] = "读写超时", ["max_response_bytes"] = "最大响应字节数" },
        "visa" => new Dictionary<string, string> { ["visa_resource"] = "VISA 资源名", ["backend_id"] = "VISA 后端", ["timeout_ms"] = "超时", ["max_response_bytes"] = "最大响应字节数", ["terminator"] = "终止符" },
        "can" => new Dictionary<string, string> { ["can_backend_id"] = "CAN 后端", ["channel"] = "CAN 通道", ["bitrate"] = "仲裁波特率", ["data_bitrate"] = "数据波特率", ["fd_enabled"] = "CAN FD", ["listen_only"] = "只听模式" },
        _ => new Dictionary<string, string>()
    };

    public static string NormalizeType(string type) => type.Trim().ToLowerInvariant() switch
    {
        "modbus_tcp" => "tcp",
        "modbus_rtu" => "serial",
        "rs232" or "rs422" or "rs485" => "serial",
        var value => value
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
