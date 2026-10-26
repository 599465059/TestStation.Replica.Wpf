using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

public partial class StepParameterField : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string FieldType { get; init; } = "text";
    public IReadOnlyList<string> Options { get; init; } = [];
    [ObservableProperty] private string _value = "";
}

public static class StepFieldCatalog
{
    private sealed record Field(string Key, string Label, string Type = "text", string Default = "", string[]? Options = null);

    private static readonly Dictionary<string, Field[]> Fields = new()
    {
        ["initialize"] = [],
        ["set_value"] = [new("channel_id", nameof(UiText.Channel)), new("value", nameof(UiText.Value), "scalar")],
        ["read_value"] = [new("channel_id", nameof(UiText.Channel)), new("timeout_ms", nameof(UiText.TimeoutMs), "integer", "0")],
        ["wait"] = [new("timeout_ms", nameof(UiText.WaitMs), "expression", "0")],
        ["assert"] = [new("channel_id", nameof(UiText.Channel)), new("comparator", nameof(UiText.Comparator), "combo", "==", ["==", "!=", ">", ">=", "<", "<=", "IN_RANGE", "OUTSIDE_RANGE", "ABS_TOL", "REL_TOL", "CONTAINS", "REGEX"]),
            new("expected", nameof(UiText.Expected), "scalar"), new("minimum", nameof(UiText.Minimum), "scalar"), new("maximum", nameof(UiText.Maximum), "scalar"),
            new("array_mode", nameof(UiText.Type), "combo", "ALL", ["ALL", "ANY", "EXACT"]), new("tolerance", nameof(UiText.Value), "scalar", "0")],
        ["if"] = [new("expression", nameof(UiText.Condition))],
        ["while"] = [new("expression", nameof(UiText.Condition)), new("max_iterations", nameof(UiText.Maximum), "expression", "1000")],
        ["loop"] = [new("variable", nameof(UiText.Variable)), new("from", nameof(UiText.From), "expression", "0"),
            new("to", nameof(UiText.To), "expression", "0"), new("step", nameof(UiText.Increment), "expression", "1"),
            new("max_iterations", nameof(UiText.Maximum), "expression", "1000")],
        ["call_sequence"] = [new("sequence_id", nameof(UiText.Sequence)), new("arguments", nameof(UiText.Arguments), "json", "{}")],
        ["log"] = [new("level", nameof(UiText.LogLevel), "combo", "INFO", ["INFO", "WARN", "ERROR", "DEBUG"]), new("message", nameof(UiText.Message))],
        ["statement"] = [new("target_variable", nameof(UiText.Variable)), new("expression", nameof(UiText.Condition))],
        ["break"] = [],
        ["continue"] = [],
        ["comment"] = []
    };

    public static IReadOnlyList<StepParameterField> Create(string kind, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values) =>
        Fields.GetValueOrDefault(kind, []).Select(field => new StepParameterField
        {
            Key = field.Key,
            Label = field.Key switch { "max_iterations" => "最大循环次数", "array_mode" => "数组判定", "tolerance" => "容差（相对为 %）", _ => UiText.Get(field.Label) },
            FieldType = field.Type,
            Options = field.Options ?? [],
            Value = values.TryGetValue(field.Key, out var value)
                ? field.Type == "scalar" ? value.GetRawText()
                    : value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
                : field.Default
        }).ToArray();

    public static bool IsKnown(string kind, string key) =>
        (kind is "set_value" or "read_value") && key == "signals" || Fields.GetValueOrDefault(kind, []).Any(field => field.Key == key);
}
