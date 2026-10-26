using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestStation.Application;

/// <summary>Shared by the editor and isolated Host; no UI or OpenTAP dependencies.</summary>
public static class SequenceValueOperations
{
    public static readonly string[] DataTypes = ["number", "integer", "boolean", "text", "number[]", "integer[]", "boolean[]", "text[]"];
    public static readonly string[] Comparators = ["NONE", "==", "!=", ">", ">=", "<", "<=", "IN_RANGE", "OUTSIDE_RANGE", "ABS_TOL", "REL_TOL", "CONTAINS", "REGEX"];
    public static readonly string[] ArrayModes = ["ALL", "ANY", "EXACT"];

    public static object? Unwrap(object? value) => value is JsonElement json ? json.ValueKind switch
    {
        JsonValueKind.Array => json.EnumerateArray().Select(v => Unwrap(v)).ToArray(),
        JsonValueKind.Number when json.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => json.GetDouble(), JsonValueKind.String => json.GetString(),
        JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Null => null,
        _ => throw new InvalidDataException("只支持标量或一维数组")
    } : value;

    public static object?[]? AsArray(object? value)
    {
        value = Unwrap(value);
        return value is IEnumerable list && value is not string ? list.Cast<object?>().Select(Unwrap).ToArray() : null;
    }

    public static double Number(object? value)
    {
        value = Unwrap(value);
        if (value is bool || AsArray(value) is not null) throw new InvalidDataException("需要数字标量");
        return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"不是有限数字: {value}");
    }

    public static object ConvertTo(object? value, string type)
    {
        value = Unwrap(value);
        if (!DataTypes.Contains(type)) throw new InvalidDataException($"未知数据类型: {type}");
        var array = AsArray(value);
        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            if (array is null) throw new InvalidDataException($"{type} 需要一维数组");
            return array.Select(v => ConvertTo(v, type[..^2])).ToArray();
        }
        if (array is not null || value is null) throw new InvalidDataException($"{type} 需要非空标量");
        if (type == "text") return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (type == "number") return Number(value);
        if (type == "integer")
        {
            if (value is long integer) return integer;
            if (value is string text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            var number = Number(value);
            if (number != Math.Truncate(number) || number < long.MinValue || number >= 9223372036854775808d)
                throw new InvalidDataException("整数超出范围或包含小数");
            return checked((long)number);
        }
        if (value is bool boolean) return boolean;
        var formatted = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (bool.TryParse(formatted, out var result)) return result;
        if (formatted is "1" or "0") return formatted == "1";
        throw new InvalidDataException("布尔值必须为 true/false 或 1/0");
    }

    public static object ParseConstant(string text, string type)
    {
        if (type == "text") return text;
        if (type.EndsWith("[]", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return ConvertTo(document.RootElement, type);
            }
            catch (JsonException error) { throw new InvalidDataException("数组格式无效，请使用 JSON，例如 [1, 2, 3]", error); }
        }
        return ConvertTo(text, type);
    }

    public static object ReadInput(string text, string type, string mode, IReadOnlyDictionary<string, object?> values) => mode switch
    {
        "constant" => ParseConstant(text, type),
        "expression" => ConvertTo(SequenceExpression.EvaluateValue(text, values), type),
        _ => throw new InvalidDataException($"未知输入方式: {mode}")
    };

    public static bool Compare(object? actual, object? expected, string comparator, string arrayMode = "ALL",
        object? minimum = null, object? maximum = null, double tolerance = 0)
    {
        if (!Comparators.Contains(comparator) || !ArrayModes.Contains(arrayMode)) throw new InvalidDataException("比较方式或数组规则无效");
        if (comparator == "NONE") return true;
        if (tolerance < 0 || !double.IsFinite(tolerance)) throw new InvalidDataException("容差必须为非负有限数");
        var readings = AsArray(actual); var expectedItems = AsArray(expected);
        var lows = AsArray(minimum); var highs = AsArray(maximum);
        var range = comparator is "IN_RANGE" or "OUTSIDE_RANGE";
        if (readings is null)
        {
            if (expectedItems is not null || lows is not null || highs is not null) return false;
            return Scalar(Unwrap(actual), Unwrap(expected), comparator, Unwrap(minimum), Unwrap(maximum), tolerance);
        }
        if (readings.Length == 0) return false; // Empty measurements do not pass by vacuous truth.
        if (arrayMode == "EXACT" && (!range && expectedItems is null || range && (lows is null || highs is null))) return false;
        if (expectedItems is not null && expectedItems.Length != readings.Length || lows is not null && lows.Length != readings.Length || highs is not null && highs.Length != readings.Length) return false;
        var results = readings.Select((reading, index) => Scalar(reading, expectedItems is null ? expected : expectedItems[index], comparator,
            lows is null ? minimum : lows[index], highs is null ? maximum : highs[index], tolerance));
        return arrayMode == "ANY" ? results.Any(v => v) : results.All(v => v);
    }

    private static bool Scalar(object? actual, object? expected, string comparator, object? minimum, object? maximum, double tolerance)
    {
        if (AsArray(actual) is not null) throw new InvalidDataException("只支持一维数组比较");
        if (comparator is "IN_RANGE" or "OUTSIDE_RANGE")
        {
            var low = Number(minimum); var high = Number(maximum);
            if (low > high) throw new InvalidDataException("比较下限不能大于上限");
            var inside = Number(actual) >= low && Number(actual) <= high;
            return comparator == "IN_RANGE" ? inside : !inside;
        }
        if (comparator is "ABS_TOL" or "REL_TOL")
            return Math.Abs(Number(actual) - Number(expected)) <= (comparator == "ABS_TOL" ? tolerance : Math.Abs(Number(expected)) * tolerance / 100);
        if (comparator is "CONTAINS" or "REGEX")
        {
            if (actual is not string text || expected is not string pattern) throw new InvalidDataException("文本比较需要文本数据");
            return comparator == "CONTAINS" ? text.Contains(pattern, StringComparison.Ordinal) : Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        var comparison = actual is bool a && expected is bool b ? a.CompareTo(b) :
            actual is string x && expected is string y ? string.CompareOrdinal(x, y) : Number(actual).CompareTo(Number(expected));
        return comparator switch { "==" => comparison == 0, "!=" => comparison != 0, ">" => comparison > 0, ">=" => comparison >= 0, "<" => comparison < 0, "<=" => comparison <= 0, _ => false };
    }

    public static string Display(object? value) => AsArray(value) is { } array ? JsonSerializer.Serialize(array) : Convert.ToString(Unwrap(value), CultureInfo.InvariantCulture) ?? "null";
}

public sealed record SequenceAssignment(string Target, string Expression);

public static class SequenceExpressionProgram
{
    public static IReadOnlyList<SequenceAssignment> Parse(string source)
    {
        if (source.Length > 32768) throw new InvalidDataException("步骤表达式内容过长");
        var result = new List<SequenceAssignment>();
        // One assignment per line. Semicolons inside text literals stay intact.
        foreach (var line in source.Replace("\r", "").Split('\n'))
        {
            var text = line.Trim().TrimEnd(';').Trim();
            if (text.Length == 0 || text.StartsWith("//", StringComparison.Ordinal)) continue;
            var match = Regex.Match(text, @"^(?<target>[_\p{L}][_\p{L}\p{N}]*(?:\.[_\p{L}][_\p{L}\p{N}]*)?)\s*=(?!=)\s*(?<expression>.+)$");
            if (!match.Success) throw new InvalidDataException($"表达式应为 变量 = 公式，每行一条：{text}");
            var expression = match.Groups["expression"].Value;
            SequenceExpression.ValidateSyntax(expression);
            result.Add(new(match.Groups["target"].Value, expression));
        }
        return result;
    }
}
