using System.Text.Json;
using TestStation.Domain.Devices;

namespace TestStation.Infrastructure.Devices.Reference;

internal static class InstrumentChannelValidation
{
    public static InstrumentChannel Find(InstrumentModel model, string id)
    {
        if (!model.RuntimeAvailable) throw new NotSupportedException($"{model.Name}：{model.Validation}");
        return model.Channels.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"型号 {model.Name} 不包含通道：{id}");
    }

    public static void ValidateValue(InstrumentChannel channel, JsonElement value)
    {
        var valid = channel.IsAction ? value.ValueKind == JsonValueKind.True : channel.DataType switch
        {
            "string" or "text" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) && double.IsFinite(n),
            _ => false
        };
        if (!valid) throw new ArgumentException($"通道 {channel.Name} 的值不符合类型 {channel.DataType}");
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (channel.Minimum.HasValue && number < channel.Minimum || channel.Maximum.HasValue && number > channel.Maximum)
                throw new ArgumentOutOfRangeException(nameof(value), $"通道 {channel.Name} 超出范围 [{channel.Minimum}, {channel.Maximum}]");
        }
    }
}
