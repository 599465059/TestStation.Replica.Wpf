using System.Globalization;
using System.Text.Json;

namespace TestStation.Application;

public static class SequenceParameterValue
{
    public static JsonElement Parse(string text, string type) => type.EndsWith("[]", StringComparison.Ordinal)
        ? JsonSerializer.SerializeToElement(SequenceValueOperations.ParseConstant(text, type)) : type switch
    {
        "text" => JsonSerializer.SerializeToElement(text),
        "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonSerializer.SerializeToElement(integer),
        "number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
        "boolean" when text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" => JsonSerializer.SerializeToElement(true),
        "boolean" when text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" => JsonSerializer.SerializeToElement(false),
        _ => throw new InvalidDataException(type switch
        {
            "integer" => "请输入整数", "number" => "请输入有限数字（小数点使用 .）",
            "boolean" => "布尔值请输入 true 或 false", _ => $"未知参数类型: {type}"
        })
    };

    public static string Display(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();

    public static bool IsReference(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        value.GetString() is { } text && text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}');

    public static void ValidateReference(string text)
    {
        if (!text.StartsWith("${", StringComparison.Ordinal) || !text.EndsWith('}') || text.Length <= 3)
            throw new InvalidDataException("变量引用格式为 ${变量名}，也可使用 ${变量名 + 1}");
        var expression = text[2..^1].Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(expression, @"^[_\p{L}][_\p{L}\p{N}.:-]*$")) return;
        SequenceExpression.ValidateSyntax(expression);
    }

    public static JsonElement Validate(JsonElement value, string type)
    {
        if (type.EndsWith("[]", StringComparison.Ordinal)) return JsonSerializer.SerializeToElement(SequenceValueOperations.ConvertTo(value, type));
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array)
            throw new InvalidDataException("参数值必须是文本、数字或布尔值");
        return Parse(Display(value), type);
    }
}
