using System.Text.RegularExpressions;

namespace TestStation.Application.Devices.ItechPowerSupply;

public static class PowerSupplyValidation
{
    public static readonly string[] Types = ["number", "integer", "boolean", "string"];
    public static readonly string[] Bindings = ["", "voltage", "current", "output", "voltage_measurement", "current_measurement", "protection"];
    public static void Validate(PowerSupplyFamily family)
    {
        if (family.Version != 2 || family.FamilyId != "itech.power-supply") throw new InvalidDataException("不支持的家族配置版本");
        Unique(family.Models.Select(m => m.Id), "型号");
        if (!family.Models.Any(m => m.Enabled)) throw new InvalidDataException("至少启用一个型号");
        ValidateChannels(family.Channels);
        foreach (var model in family.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Name) || !double.IsFinite(model.MaxVoltage) || !double.IsFinite(model.MaxCurrent) || !double.IsFinite(model.MaxPower) || model.MaxVoltage <= 0 || model.MaxCurrent <= 0 || model.MaxPower <= 0 || model.ChannelCount < 1)
                throw new InvalidDataException($"型号 {model.Id} 的名称或量程无效");
            if (model.ProtocolId != "scpi" || model.DriverId != "itech.public-scpi" || !model.Tcp && !model.Serial) throw new InvalidDataException($"型号 {model.Name} 的驱动、协议或通信方式无效");
            var channels = family.Resolve(model); ValidateChannels(channels);
            foreach (var role in new[] { ("voltage", model.Voltage), ("current", model.Current), ("output", model.Output), ("protection", model.Protection) })
                if (role.Item2 && !channels.Any(c => c.Enabled && c.Binding == role.Item1 && c.CanWrite)) throw new InvalidDataException($"型号 {model.Name} 缺少可写的 {role.Item1} 功能绑定");
        }
    }
    public static void ValidateChannels(IReadOnlyList<PowerSupplyChannel> channels)
    {
        Unique(channels.Select(c => c.Id), "通道");
        var bound = new HashSet<string>();
        foreach (var c in channels)
        {
            if (string.IsNullOrWhiteSpace(c.DisplayName) || !Types.Contains(c.DataType) || !Bindings.Contains(c.Binding)) throw new InvalidDataException($"通道 {c.Id} 名称、类型或绑定无效");
            if (!c.CanRead && !c.CanWrite || c.Poll && !c.CanRead) throw new InvalidDataException($"通道 {c.Id} 访问权限或轮询无效");
            if (c.Enabled && c.Binding.Length > 0 && !bound.Add(c.Binding)) throw new InvalidDataException($"功能绑定重复：{c.Binding}");
            if (c.Binding is "voltage" or "current" or "voltage_measurement" or "current_measurement" && c.DataType != "number") throw new InvalidDataException("电压电流绑定必须是数字类型");
            if (c.Binding == "output" && c.DataType != "boolean") throw new InvalidDataException("输出开关必须是布尔类型");
            if (c.CanRead) Command(c.ReadCommand);
            if (c.CanWrite) { Command(c.WriteCommand); if (!c.WriteCommand!.Contains("{value}")) throw new InvalidDataException($"通道 {c.Id} 写入模板缺少 {{value}}"); if (c.WriteCommand.Replace("{value}", "").IndexOfAny(['{','}']) >= 0) throw new InvalidDataException("不支持的命令占位符"); }
            if (!double.IsFinite(c.Scale) || c.Scale == 0 || !double.IsFinite(c.Offset) || c.Minimum.HasValue && !double.IsFinite(c.Minimum.Value) || c.Maximum.HasValue && !double.IsFinite(c.Maximum.Value) || c.Minimum > c.Maximum)
                throw new InvalidDataException($"通道 {c.Id} 范围或换算无效");
            if (c.IntervalMs < 50 || c.TimeoutMs < 1 || c.TimeoutMs > 60000) throw new InvalidDataException($"通道 {c.Id} 轮询周期或超时无效");
            if (c.DataType == "boolean")
            {
                var yes = c.TrueResponse.Split('|'); var no = c.FalseResponse.Split('|');
                if (yes.Any(string.IsNullOrWhiteSpace) || no.Any(string.IsNullOrWhiteSpace) || yes.Intersect(no, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException("布尔响应映射为空或冲突");
                Command(c.TrueCommand); Command(c.FalseCommand);
            }
        }
    }
    private static void Command(string? command)
    { if (string.IsNullOrWhiteSpace(command) || command.Any(c => c < 32 || c > 126)) throw new InvalidDataException("SCPI 指令必须是非空 ASCII 单行文本"); }
    private static void Unique(IEnumerable<string> ids, string kind)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids) if (id is null || !Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_.-]*$") || !seen.Add(id)) throw new InvalidDataException($"{kind}标识无效或重复：{id}");
    }
}
