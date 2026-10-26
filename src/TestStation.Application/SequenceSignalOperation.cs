using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TestStation.Application;

public sealed record SequenceSignalOperation
{
    [JsonPropertyName("channel_id")] public string ChannelId { get; init; } = "";
    [JsonPropertyName("data_type")] public string DataType { get; init; } = "number";
    [JsonPropertyName("mode")] public string Mode { get; init; } = "constant";
    [JsonPropertyName("value")] public string Value { get; init; } = "";
    [JsonPropertyName("comparator")] public string Comparator { get; init; } = "NONE";
    [JsonPropertyName("expected")] public string Expected { get; init; } = "";
    [JsonPropertyName("minimum")] public string Minimum { get; init; } = "";
    [JsonPropertyName("maximum")] public string Maximum { get; init; } = "";
    [JsonPropertyName("array_mode")] public string ArrayMode { get; init; } = "ALL";
    [JsonPropertyName("tolerance")] public string Tolerance { get; init; } = "0";

    public static SequenceSignalOperation[] Read(JsonElement signals)
    {
        if (signals.ValueKind != JsonValueKind.Array) throw new InvalidDataException("信号表必须是数组");
        var rows = signals.Deserialize<SequenceSignalOperation[]>() ?? [];
        if (rows.Any(row => row is null || row.ChannelId is null || row.DataType is null || row.Mode is null ||
            row.Value is null || row.Comparator is null || row.Expected is null || row.Minimum is null ||
            row.Maximum is null || row.ArrayMode is null || row.Tolerance is null))
            throw new InvalidDataException("信号表行和字段不能为 null");
        return rows;
    }

    public void Validate(bool read, IEnumerable<string>? knownNames = null)
    {
        if (string.IsNullOrWhiteSpace(ChannelId)) throw new InvalidDataException("请选择信号通道");
        if (!SequenceValueOperations.DataTypes.Contains(DataType)) throw new InvalidDataException($"数据类型无效: {DataType}");
        if (Mode is not ("constant" or "expression")) throw new InvalidDataException("输入方式无效");
        if (!read) { CheckInput(Value, false); return; }
        if (!SequenceValueOperations.Comparators.Contains(Comparator) || !SequenceValueOperations.ArrayModes.Contains(ArrayMode)) throw new InvalidDataException("比较规则无效");
        if (Comparator == "NONE") return;
        var array = DataType.EndsWith("[]", StringComparison.Ordinal);
        var baseType = array ? DataType[..^2] : DataType;
        if ((Comparator is "IN_RANGE" or "OUTSIDE_RANGE" or "ABS_TOL" or "REL_TOL") && baseType is not ("number" or "integer"))
            throw new InvalidDataException("范围和容差比较只适用于数字类型");
        if ((Comparator is "CONTAINS" or "REGEX") && baseType != "text") throw new InvalidDataException("包含和正则比较只适用于文本类型");
        if (baseType == "boolean" && Comparator is not ("==" or "!=")) throw new InvalidDataException("布尔值仅支持等于或不等于比较");
        if (Comparator is "IN_RANGE" or "OUTSIDE_RANGE") { CheckInput(Minimum, true); CheckInput(Maximum, true); }
        else CheckInput(Expected, true);
        if (Comparator is "ABS_TOL" or "REL_TOL")
        {
            if (Mode == "expression") CheckExpression(Tolerance);
            else if (SequenceValueOperations.Number(Tolerance) < 0) throw new InvalidDataException("容差不能小于 0");
        }
        if (Mode == "constant")
        {
            var values = new Dictionary<string, object?>();
            var range = Comparator is "IN_RANGE" or "OUTSIDE_RANGE";
            if (array && ArrayMode == "EXACT" && (range
                ? SequenceValueOperations.AsArray(RuleInput(Minimum, values)) is null || SequenceValueOperations.AsArray(RuleInput(Maximum, values)) is null
                : SequenceValueOperations.AsArray(RuleInput(Expected, values)) is null))
                throw new InvalidDataException("数组逐项比较需要期望数组（范围比较需上下限数组）；使用标量约束请选全部或任一元素");
            if (range)
            {
                var low = RuleInput(Minimum, values); var high = RuleInput(Maximum, values);
                var lows = SequenceValueOperations.AsArray(low); var highs = SequenceValueOperations.AsArray(high);
                if (lows is not null && highs is not null && lows.Length != highs.Length) throw new InvalidDataException("上下限数组长度必须一致");
                for (var index = 0; index < (lows?.Length ?? highs?.Length ?? 1); index++)
                    if (SequenceValueOperations.Number(lows is null ? low : lows[index]) > SequenceValueOperations.Number(highs is null ? high : highs[index]))
                        throw new InvalidDataException("比较下限不能大于上限");
            }
            if (Comparator == "REGEX")
            {
                var expected = RuleInput(Expected, values);
                foreach (var pattern in SequenceValueOperations.AsArray(expected) ?? [expected])
                    _ = new Regex((string)pattern!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
        }

        void CheckInput(string text, bool rule)
        {
            if (Mode == "expression") { CheckExpression(text); return; }
            var type = rule && DataType.EndsWith("[]", StringComparison.Ordinal) && !text.TrimStart().StartsWith('[') ? DataType[..^2] : DataType;
            SequenceValueOperations.ParseConstant(text, type);
        }
        void CheckExpression(string text)
        {
            if (knownNames is null) SequenceExpression.ValidateSyntax(text);
            else SequenceExpression.ValidateReferences(text, knownNames);
        }
    }

    public object RuleInput(string text, IReadOnlyDictionary<string, object?> values)
    {
        var baseType = DataType.EndsWith("[]", StringComparison.Ordinal) ? DataType[..^2] : DataType;
        if (Mode == "expression")
        {
            var value = SequenceExpression.EvaluateValue(text, values);
            return SequenceValueOperations.ConvertTo(value, SequenceValueOperations.AsArray(value) is null ? baseType : baseType + "[]");
        }
        return SequenceValueOperations.ParseConstant(text, text.TrimStart().StartsWith('[') && DataType.EndsWith("[]", StringComparison.Ordinal) ? DataType : baseType);
    }
}
