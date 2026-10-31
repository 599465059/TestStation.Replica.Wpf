using System.Globalization;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Hardware;

namespace TestStation.Infrastructure.Devices.ItechPowerSupply;

public sealed class ItechPowerSupplyDriver : IDeviceDriver
{
    private readonly IProtocolSession _protocol;
    private readonly ItechPowerSupplyProfile _profile;
    private bool _disposed;
    public ItechPowerSupplyDriver(string deviceId, IProtocolSession protocol, ItechPowerSupplyProfile? profile = null)
    { DeviceId = deviceId; _protocol = protocol; _profile = profile ?? ItechPowerSupplyProfile.PublicScpi; }
    public string DeviceId { get; }
    public string FamilyId => _profile.FamilyId;
    public string ModelId => _profile.ModelId;
    public IReadOnlyList<SequenceChannelDefinition> Channels => _profile.Channels.Select(c => new SequenceChannelDefinition(c.Id, c.DataType, _profile.Name, c.CanRead, c.CanWrite)).ToArray();

    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default)
    {
        var channel = _profile.Channel(channelId);
        if (!channel.Enabled || !channel.CanRead) throw new InvalidOperationException($"通道只写：{channel.DisplayName}");
        ObjectDisposedException.ThrowIf(_disposed, this);
        var text = (await _protocol.QueryAsync(channel.ReadCommand, cancellationToken).ConfigureAwait(false)).Trim();
        if (channel.DataType == "string") return JsonSerializer.SerializeToElement(text);
        if (channel.DataType == "boolean")
        {
            if (channel.TrueResponse.Split('|').Contains(text, StringComparer.OrdinalIgnoreCase)) return JsonSerializer.SerializeToElement(true);
            if (channel.FalseResponse.Split('|').Contains(text, StringComparer.OrdinalIgnoreCase)) return JsonSerializer.SerializeToElement(false);
            throw new InvalidDataException($"通道 {channel.DisplayName} 返回未知布尔值：{text}");
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new InvalidDataException($"设备返回的数值无效：{text}");
        number = number * channel.Scale + channel.Offset;
        if (!double.IsFinite(number)) throw new InvalidDataException("换算结果非有限值");
        if (channel.DataType == "integer")
        {
            if (number != Math.Truncate(number) || number < long.MinValue || number >= (double)long.MaxValue) throw new InvalidDataException("设备返回了非整数或超出范围");
            return JsonSerializer.SerializeToElement((long)number);
        }
        return JsonSerializer.SerializeToElement(number);
    }

    public async Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channel = _profile.Channel(channelId);
        if (!channel.Enabled || !channel.CanWrite || string.IsNullOrWhiteSpace(channel.WriteCommand)) throw new InvalidOperationException($"通道不可写：{channel.DisplayName}");
        string commandValue;
        if (channel.DataType == "boolean") commandValue = value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() ? channel.TrueCommand : channel.FalseCommand : throw new InvalidDataException("写入值必须为布尔类型");
        else if (channel.DataType == "string") commandValue = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("写入值必须为文本类型");
        else
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("写入值必须为有限数值");
            if (channel.DataType == "integer" && (!value.TryGetInt64(out _) || number != Math.Truncate(number))) throw new InvalidDataException("写入值必须为整数");
            var max = channel.Maximum;
            if (channel.Binding == "voltage") max = Math.Min(max ?? _profile.MaxVoltage, _profile.MaxVoltage);
            if (channel.Binding == "current") max = Math.Min(max ?? _profile.MaxCurrent, _profile.MaxCurrent);
            if (channel.Minimum.HasValue && number < channel.Minimum || max.HasValue && number > max) throw new InvalidOperationException($"{channel.DisplayName} 超出允许范围");
            var raw = (number - channel.Offset) / channel.Scale;
            if (!double.IsFinite(raw)) throw new InvalidDataException("写入换算无效");
            commandValue = raw.ToString("G17", CultureInfo.InvariantCulture);
        }
        if (commandValue.Any(c => c < 32 || c > 126 || c == ';')) throw new InvalidDataException("写入内容包含非法 SCPI 控制字符或分隔符");
        await _protocol.SendAsync(channel.WriteCommand.Replace("{value}", commandValue, StringComparison.Ordinal), cancellationToken).ConfigureAwait(false);
        var error = await _protocol.QueryAsync("SYST:ERR?", cancellationToken).ConfigureAwait(false);
        var code = error.Split(',')[0].Trim();
        if (!int.TryParse(code, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed != 0) throw new InvalidOperationException($"设备拒绝操作：{error}");
    }

    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _protocol.DisposeAsync().ConfigureAwait(false); }
}
