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
        ["set_value"] = [new("channel_id", nameof(UiText.Channel)), new("value", nameof(UiText.Value))],
        ["read_value"] = [new("channel_id", nameof(UiText.Channel)), new("timeout_ms", nameof(UiText.TimeoutMs), "integer", "0")],
        ["wait"] = [new("timeout_ms", nameof(UiText.WaitMs), "integer", "0")],
        ["assert"] = [new("channel_id", nameof(UiText.Channel)), new("comparator", nameof(UiText.Comparator), "combo", "==", ["==", "!=", ">", ">=", "<", "<=", "IN_RANGE"]),
            new("expected", nameof(UiText.Expected)), new("minimum", nameof(UiText.Minimum)), new("maximum", nameof(UiText.Maximum)),
            new("failure_action", nameof(UiText.OnFailure), "combo", "STOP", ["STOP", "CONTINUE", "LOG"])],
        ["if"] = [new("expression", nameof(UiText.Condition))],
        ["loop"] = [new("variable", nameof(UiText.Variable)), new("from", nameof(UiText.From), "text", "0"),
            new("to", nameof(UiText.To), "text", "0"), new("step", nameof(UiText.Increment), "text", "1")],
        ["call_sequence"] = [new("sequence_id", nameof(UiText.Sequence)), new("arguments", nameof(UiText.Arguments), "json", "{}")],
        ["comment"] = []
    };

    public static IReadOnlyList<StepParameterField> Create(string kind, IReadOnlyDictionary<string, System.Text.Json.JsonElement> values) =>
        Fields.GetValueOrDefault(kind, []).Select(field => new StepParameterField
        {
            Key = field.Key,
            Label = UiText.Get(field.Label),
            FieldType = field.Type,
            Options = field.Options ?? [],
            Value = values.TryGetValue(field.Key, out var value)
                ? value.ValueKind == System.Text.Json.JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()
                : field.Default
        }).ToArray();

    public static bool IsKnown(string kind, string key) =>
        Fields.GetValueOrDefault(kind, []).Any(field => field.Key == key);
}
