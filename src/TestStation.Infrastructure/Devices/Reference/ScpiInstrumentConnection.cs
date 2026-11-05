using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Scpi;

namespace TestStation.Infrastructure.Devices.Reference;

internal sealed class ScpiInstrumentConnection : InstrumentConnection
{
    private readonly ScpiCommandSet _definition;
    private readonly ProfileScpiProtocol _protocol;
    private readonly Dictionary<string, (ScpiParameter Parameter, int Channel)> _parameters;
    private readonly Dictionary<string, (ScpiAction Action, int Channel)> _actions;
    private bool _opened;
    private bool _disposed;
    public ScpiInstrumentConnection(ScpiCommandSet definition, IByteTransportSession transport, TransportResource resource,
        Dictionary<string, (ScpiParameter, int)> parameters, Dictionary<string, (ScpiAction, int)> actions,
        Action<string, string> traffic)
    {
        _definition = definition; _parameters = parameters; _actions = actions;
        _protocol = new(transport, resource, definition.Terminator, definition.Upper, definition.DelayMs, traffic);
    }
    public override async Task OpenAsync(CancellationToken token)
    {
        await _protocol.OpenAsync(token).ConfigureAwait(false);
        foreach (var command in _definition.Connect)
            await _protocol.ExecuteAsync(command, false, _definition.TimeoutMs, token).ConfigureAwait(false);
        _opened = true;
    }
    private async Task SelectAsync(int channel, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_definition.ChannelSelect))
            await _protocol.ExecuteAsync(_definition.ChannelSelect.Replace("{channel}", channel.ToString(CultureInfo.InvariantCulture)),
                false, _definition.TimeoutMs, token).ConfigureAwait(false);
    }
    public override async Task<JsonElement> ReadAsync(string localChannel, CancellationToken token)
    {
        if (_actions.TryGetValue(localChannel, out var entry))
        {
            if (entry.Action.CommandType != "Query") throw new InvalidOperationException("动作通道只写");
            await SelectAsync(entry.Channel, token).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(await _protocol.ExecuteAsync(entry.Action.CommandText, true, entry.Action.TimeoutMilliseconds, token).ConfigureAwait(false));
        }
        var (parameter, channel) = _parameters.GetValueOrDefault(localChannel);
        if (parameter is null) throw new KeyNotFoundException($"未知通道：{localChannel}");
        if (string.IsNullOrWhiteSpace(parameter.QueryCommand)) throw new InvalidOperationException("通道不可读");
        await SelectAsync(channel, token).ConfigureAwait(false);
        var response = (await _protocol.ExecuteAsync(parameter.QueryCommand, true, parameter.TimeoutMilliseconds, token).ConfigureAwait(false))!;
        if (parameter.DelayAfterMilliseconds > 0) await Task.Delay(parameter.DelayAfterMilliseconds, token).ConfigureAwait(false);
        return Parse(parameter, response);
    }
    internal static JsonElement Parse(ScpiParameter parameter, string response)
    {
        var text = response.Trim();
        if (!string.IsNullOrWhiteSpace(parameter.ResponseRegex))
        {
            var match = Regex.Match(text, parameter.ResponseRegex, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (!match.Success) throw new InvalidDataException($"{parameter.DisplayName} 响应格式不匹配：{text}");
            text = string.IsNullOrEmpty(parameter.ResponseGroup) ? match.Value : match.Groups[parameter.ResponseGroup].Value;
        }
        if (parameter.ResponseMap.Count > 0)
        {
            var mapped = parameter.ResponseMap.FirstOrDefault(p => p.Key.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (mapped.Key is null) throw new InvalidDataException($"未知响应映射：{text}");
            text = mapped.Value.ToString();
        }
        if (parameter.Type == "string") return JsonSerializer.SerializeToElement(text);
        if (parameter.Type == "boolean") return text.ToUpperInvariant() switch
        {
            "1" or "ON" or "TRUE" => JsonSerializer.SerializeToElement(true),
            "0" or "OFF" or "FALSE" => JsonSerializer.SerializeToElement(false),
            _ => throw new InvalidDataException($"未知布尔响应：{text}")
        };
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            throw new InvalidDataException($"无效数值响应：{text}");
        value = value * parameter.ResponseScale + parameter.ResponseOffset;
        if (!double.IsFinite(value)) throw new InvalidDataException("响应换算结果不是有限数值");
        if (parameter.Type == "integer")
        {
            if (value != Math.Truncate(value) || value < long.MinValue || value >= (double)long.MaxValue) throw new InvalidDataException("响应不是有效整数");
            return JsonSerializer.SerializeToElement((long)value);
        }
        return JsonSerializer.SerializeToElement(value);
    }
    public override async Task WriteAsync(string localChannel, JsonElement value, CancellationToken token)
    {
        if (_actions.TryGetValue(localChannel, out var action))
        {
            if (action.Action.CommandType != "Command" || value.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("执行动作必须写入布尔值 true");
            await SelectAsync(action.Channel, token).ConfigureAwait(false);
            await _protocol.ExecuteAsync(action.Action.CommandText, false, action.Action.TimeoutMilliseconds, token).ConfigureAwait(false);
            if (action.Action.DelayAfterMilliseconds > 0) await Task.Delay(action.Action.DelayAfterMilliseconds, token).ConfigureAwait(false);
            return;
        }
        var (parameter, channel) = _parameters.GetValueOrDefault(localChannel);
        if (parameter is null) throw new KeyNotFoundException($"未知通道：{localChannel}");
        if (parameter.IsReadOnly || string.IsNullOrWhiteSpace(parameter.WriteCommand)) throw new InvalidOperationException("通道不可写");
        var formatted = Format(parameter, value);
        await SelectAsync(channel, token).ConfigureAwait(false);
        await _protocol.ExecuteAsync(parameter.WriteCommand.Replace("{value}", formatted, StringComparison.Ordinal), false,
            parameter.TimeoutMilliseconds, token).ConfigureAwait(false);
        if (parameter.DelayAfterMilliseconds > 0) await Task.Delay(parameter.DelayAfterMilliseconds, token).ConfigureAwait(false);
    }
    internal static string Format(ScpiParameter parameter, JsonElement value)
    {
        if (parameter.Type == "boolean") return value.ValueKind switch
        { JsonValueKind.True => "ON", JsonValueKind.False => "OFF", _ => throw new InvalidDataException("值必须为布尔类型") };
        if (parameter.Type == "string")
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("值必须为文本类型");
            var text = value.GetString()!;
            if (text.Any(c => c < 32 || c > 126 || c == ';')) throw new InvalidDataException("写入内容包含非法 SCPI 分隔符或字符");
            return text;
        }
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("值必须为有限数值");
        if (parameter.Type == "integer" && !value.TryGetInt64(out _)) throw new InvalidDataException("值必须为整数");
        if (parameter.MinValue.HasValue && number < parameter.MinValue || parameter.MaxValue.HasValue && number > parameter.MaxValue)
            throw new InvalidDataException($"{parameter.DisplayName} 超出范围 [{parameter.MinValue}, {parameter.MaxValue}]");
        // Reference ResponseScale/Offset describe reads only; writes are already engineering values.
        return number.ToString("G17", CultureInfo.InvariantCulture);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        try
        {
            if (_opened)
                foreach (var command in _definition.Disconnect)
                    await _protocol.ExecuteAsync(command, false, Math.Min(_definition.TimeoutMs, 1000), CancellationToken.None).ConfigureAwait(false);
        }
        finally { _opened = false; await _protocol.DisposeAsync().ConfigureAwait(false); }
    }
}
