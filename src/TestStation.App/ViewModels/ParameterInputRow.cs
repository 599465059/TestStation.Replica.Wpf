using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Application;
using TestStation.Domain;
using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

public partial class ParameterInputRow : ObservableObject
{
    public SequenceVariable Definition { get; }
    public string Name => Definition.Name;
    public string TypeLabel => UiDisplay.Option(Definition.DataType);
    public string DefaultLabel => Definition.Default is { ValueKind: not JsonValueKind.Null } value
        ? $"默认值：{SequenceParameterValue.Display(value)}" : "必填（无默认值）";
    public IReadOnlyList<string> Modes { get; }
    public IReadOnlyList<string> Suggestions { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsInputEnabled))] private string _mode = "使用默认值";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _error = "";
    public bool IsInputEnabled => Mode != "使用默认值";

    public ParameterInputRow(SequenceVariable definition, bool allowReferences = false,
        JsonElement? supplied = null, IReadOnlyList<string>? suggestions = null)
    {
        Definition = definition;
        Modes = allowReferences ? ["使用默认值", "常量", "变量引用"] : ["使用默认值", "常量"];
        Suggestions = suggestions ?? [];
        if (supplied is { } initial)
        {
            Mode = allowReferences && SequenceParameterValue.IsReference(initial) ? "变量引用" : "常量";
            Value = SequenceParameterValue.Display(initial);
        }
        else if (definition.Default is null || definition.Default.Value.ValueKind == JsonValueKind.Null) Mode = "常量";
    }

    partial void OnModeChanged(string value) => Error = "";
    partial void OnValueChanged(string value) => Error = "";

    public JsonElement? ReadValue()
    {
        try
        {
            Error = "";
            if (Mode == "使用默认值")
            {
                if (Definition.Default is not { } initial || initial.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException("此参数没有默认值，请填写常量或变量引用");
                SequenceParameterValue.Validate(initial, Definition.DataType);
                return null;
            }
            if (Mode == "变量引用")
            {
                var text = Value.Trim();
                SequenceParameterValue.ValidateReference(text);
                return JsonSerializer.SerializeToElement(text);
            }
            return SequenceParameterValue.Parse(Value, Definition.DataType);
        }
        catch (InvalidDataException error) { Error = error.Message; throw new InvalidDataException($"{Name}: {error.Message}", error); }
    }
}
