using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    [ObservableProperty] private SignalValueRow? _selectedSignalValueRow;
    [ObservableProperty] private string _expressionValidationMessage = "";
    public ObservableCollection<string> SignalChannelChoices { get; } = [];
    public ObservableCollection<SequenceChannelDefinition> SignalChannelDefinitions { get; } = [];
    private readonly IReadOnlyList<ISequenceChannelProvider> _channelProviders;
    private IReadOnlyList<SequenceChannelDefinition> _allSignalChannels = [];
    public bool IsReadSignalTableStep => SelectedRow?.Step?.Kind == "read_value";

    private void RefreshSignalChannels()
    {
        var simulation = _workspace is null ? [] : _workspaceService.LoadChannelDefinitions(_workspace);
        _allSignalChannels = simulation.Concat(_channelProviders.SelectMany(provider => provider.GetChannels())).ToArray();
        if (_allSignalChannels.Select(channel => channel.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != _allSignalChannels.Count)
            throw new InvalidDataException("通道 ID 重复，请使用包含插件或设备名称的唯一通道 ID");
        if (_allSignalChannels.Any(channel => string.IsNullOrWhiteSpace(channel.Id) || !SequenceValueOperations.DataTypes.Contains(channel.DataType)))
            throw new InvalidDataException("通道定义的名称或数据类型无效");
        FilterSignalChannels();
    }

    private void FilterSignalChannels()
    {
        SignalChannelChoices.Clear(); SignalChannelDefinitions.Clear();
        foreach (var channel in _allSignalChannels.Where(channel => IsReadSignalTableStep ? channel.CanRead : channel.CanWrite).OrderBy(channel => channel.Id))
        {
            SignalChannelChoices.Add(channel.Id); SignalChannelDefinitions.Add(channel);
        }
    }

    private SequenceChannelDefinition? FindSignalChannel(string id) =>
        SignalChannelDefinitions.FirstOrDefault(channel => channel.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static SequenceSignalOperation SignalOperation(SignalValueRow row) => new()
    {
        ChannelId = row.Target.Trim(), DataType = row.DataType, Mode = row.Mode, Value = row.Value,
        Comparator = row.Comparator, Expected = row.Expected, Minimum = row.Minimum, Maximum = row.Maximum,
        ArrayMode = row.ArrayMode, Tolerance = row.Tolerance
    };

    private void LoadSignalTable(SequenceStep? step)
    {
        FilterSignalChannels();
        foreach (var row in SignalValueRows) row.PropertyChanged -= SignalRowChanged;
        SignalValueRows.Clear(); SelectedSignalValueRow = null;
        ExpressionValidationMessage = "";
        if (step?.Kind is "set_value" or "read_value")
        {
            if (step.Params.TryGetValue("signals", out var signals))
                foreach (var source in SequenceSignalOperation.Read(signals))
                    AddSignalEditorRow(new SignalValueRow { Target = source.ChannelId, DataType = source.DataType, Mode = source.Mode,
                        Value = source.Value, Comparator = source.Comparator, Expected = source.Expected, Minimum = source.Minimum,
                        Maximum = source.Maximum, ArrayMode = source.ArrayMode, Tolerance = source.Tolerance });
            else
            {
                var channel = step.Params.GetValueOrDefault("channel_id").ToString();
                if (string.IsNullOrWhiteSpace(channel))
                {
                    OnPropertyChanged(nameof(IsSignalTableStep)); OnPropertyChanged(nameof(IsReadSignalTableStep));
                    return;
                }
                var value = step.Params.GetValueOrDefault("value");
                var definition = SimulationValues.FirstOrDefault(v => v.Channel == channel);
                var declaredChannel = FindSignalChannel(channel);
                var type = InferLegacySignalType(step, value, definition?.Value);
                if (declaredChannel is not null) type = declaredChannel.DataType;
                var reference = value.ValueKind == JsonValueKind.String && SequenceParameterValue.IsReference(value);
                AddSignalEditorRow(new SignalValueRow { Target = channel, DataType = type,
                    Value = step.Kind == "read_value" ? "" : value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "0" : SequenceParameterValue.Display(value),
                    Mode = reference ? "expression" : "constant" });
            }
            SelectedSignalValueRow = SignalValueRows.FirstOrDefault();
        }
        OnPropertyChanged(nameof(IsSignalTableStep)); OnPropertyChanged(nameof(IsReadSignalTableStep));
    }

    private static string InferSignalType(JsonElement? value)
    {
        if (value is not { } json) return "number";
        if (json.ValueKind == JsonValueKind.Array)
            return (json.GetArrayLength() == 0 ? "number" : InferSignalType(json[0])) + "[]";
        return json.ValueKind switch { JsonValueKind.String => "text", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "number" };
    }

    private string InferLegacySignalType(SequenceStep step, JsonElement value, JsonElement? channelValue)
    {
        // A legacy SET VALUE reference is serialized as text (for example ${Locals.limit});
        // use the declared variable type before falling back to the literal/channel value.
        if (step.Kind == "set_value" && SequenceParameterValue.IsReference(value))
        {
            var expression = value.GetString()![2..^1].Trim();
            var name = expression;
            if (name.StartsWith("Locals.", StringComparison.OrdinalIgnoreCase)) name = name[7..];
            var declared = _sequence?.Variables.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (declared is not null) return declared.DataType;
            var globalName = expression.StartsWith("Globals.", StringComparison.OrdinalIgnoreCase) ? expression[8..] : "";
            declared = _activeSubproject?.GlobalVariables.FirstOrDefault(v => v.Name.Equals(globalName, StringComparison.OrdinalIgnoreCase));
            if (declared is not null) return declared.DataType;
            var stationName = expression.StartsWith("Station.", StringComparison.OrdinalIgnoreCase) ? expression[8..] : "";
            declared = _stationVariables.FirstOrDefault(v => v.Name.Equals(stationName, StringComparison.OrdinalIgnoreCase));
            return declared?.DataType ?? InferSignalType(channelValue);
        }
        return InferSignalType(step.Kind == "set_value" && value.ValueKind != JsonValueKind.Undefined ? value : channelValue);
    }

    private void AddSignalEditorRow(SignalValueRow row)
    {
        if (FindSignalChannel(row.Target) is { } channel) row.DataType = channel.DataType;
        row.PropertyChanged += SignalRowChanged; SignalValueRows.Add(row);
    }
    private void SignalRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SignalValueRow.Target) && sender is SignalValueRow row)
            row.DataType = FindSignalChannel(row.Target)?.DataType ?? "";
        OnEditorChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void AddSignalRow()
    {
        if (!CanEditSequence || !IsSignalTableStep) return;
        var row = new SignalValueRow();
        AddSignalEditorRow(row); SelectedSignalValueRow = row; OnEditorChanged();
    }

    /// <summary>Add one empty row per selected channel; the channel metadata drives its data type.</summary>
    public void AddSignalRows(IEnumerable<string> channels)
    {
        if (!CanEditSequence || !IsSignalTableStep) return;
        var existing = SignalValueRows.Select(row => row.Target).ToHashSet(StringComparer.OrdinalIgnoreCase);
        SignalValueRow? last = null;
        foreach (var channel in channels.Where(channel => !string.IsNullOrWhiteSpace(channel)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (existing.Contains(channel) || FindSignalChannel(channel) is not { } definition) continue;
            var row = new SignalValueRow { Target = channel };
            row.DataType = definition.DataType;
            AddSignalEditorRow(row); existing.Add(channel); last = row;
        }
        if (last is not null) SelectedSignalValueRow = last;
        OnEditorChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void RemoveSignalRow(SignalValueRow? target)
    {
        if (!CanEditSequence || (target ?? SelectedSignalValueRow) is not { } row || !SignalValueRows.Contains(row)) return;
        row.PropertyChanged -= SignalRowChanged; SignalValueRows.Remove(row);
        SelectedSignalValueRow = SignalValueRows.FirstOrDefault(); OnEditorChanged();
    }

    private void ApplySignalTable(Dictionary<string, JsonElement> parameters, string kind)
    {
        var rows = SignalValueRows.Select(SignalOperation).ToArray();
        foreach (var row in rows)
        {
            var definition = FindSignalChannel(row.ChannelId) ?? throw new InvalidDataException($"通道不可用：{row.ChannelId}，请检查通道定义或插件");
            if (row.DataType != definition.DataType) throw new InvalidDataException($"{row.ChannelId} 的数据类型必须由通道定义为 {definition.DataType}");
            row.Validate(kind == "read_value");
        }
        parameters["signals"] = JsonSerializer.SerializeToElement(rows);
        parameters.Remove("channel_id"); parameters.Remove("value");
    }

    private HashSet<string> VariableNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in _sequence?.Variables ?? []) { names.Add(variable.Name); names.Add("Locals." + variable.Name); }
        foreach (var variable in _activeSubproject?.GlobalVariables ?? []) names.Add("Globals." + variable.Name);
        foreach (var variable in _stationVariables) names.Add("Station." + variable.Name);
        return names;
    }

    private HashSet<string> ExpressionNames()
    {
        var names = VariableNames();
        foreach (var channel in SimulationValues)
        {
            names.Add(channel.Channel);
            names.Add(string.Concat(channel.Channel.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])));
        }
        foreach (var channel in _allSignalChannels)
        {
            names.Add(channel.Id);
            names.Add(string.Concat(channel.Id.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])));
        }
        if (_sequence is not null && SelectedRow?.Step is { } selected)
        {
            AddAncestorLoops(_sequence.SetupSteps, []);
            AddAncestorLoops(_sequence.Steps, []);
            AddAncestorLoops(_sequence.CleanupSteps, []);
            void AddAncestorLoops(IEnumerable<SequenceStep> steps, IReadOnlyList<string> ancestors)
            {
                foreach (var step in steps)
                {
                    if (ReferenceEquals(step, selected))
                        foreach (var name in ancestors) { names.Add(name); names.Add("Locals." + name); }
                    IReadOnlyList<string> children = step.Kind == "loop"
                        ? [.. ancestors, step.Params.GetValueOrDefault("variable").ToString()] : ancestors;
                    AddAncestorLoops(step.Children, children);
                    AddAncestorLoops(step.ElseChildren, ancestors);
                }
            }
        }
        return names;
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ValidateStepExpressions()
    {
        if (!CanEditSequence || SelectedRow?.Step is not { } step) return;
        try
        {
            var update = BuildStepUpdate(step);
            if (IsSignalTableStep && SignalValueRows.Count == 0) throw new InvalidDataException("请选择至少一个通道；空信号表可以保存，运行前必须补齐");
            var names = ExpressionNames(); var variables = VariableNames();
            foreach (var source in new[] { update.BeforeExpression, update.AfterExpression })
                foreach (var assignment in SequenceExpressionProgram.Parse(source))
                {
                    if (!variables.Contains(assignment.Target)) throw new InvalidDataException($"赋值目标未声明: {assignment.Target}");
                    SequenceExpression.ValidateReferences(assignment.Expression, names);
                }
            if (!string.IsNullOrWhiteSpace(update.Precondition)) SequenceExpression.ValidateReferences(update.Precondition, names);
            foreach (var field in StepFields.Where(f => f.FieldType == "expression" || f.Key == "expression"))
                if (!string.IsNullOrWhiteSpace(field.Value)) SequenceExpression.ValidateReferences(field.Value, names);
            if (step.Kind == "statement")
            {
                var target = StepFields.FirstOrDefault(f => f.Key == "target_variable")?.Value.Trim() ?? "";
                if (!variables.Contains(target)) throw new InvalidDataException($"赋值目标未声明: {target}");
            }
            foreach (var field in StepFields.Where(f => f.FieldType == "scalar" && !string.IsNullOrWhiteSpace(f.Value)))
            {
                if (step.Kind == "assert")
                {
                    var comparator = StepFields.FirstOrDefault(f => f.Key == "comparator")?.Value ?? "==";
                    var range = comparator is "IN_RANGE" or "OUTSIDE_RANGE";
                    if (field.Key == "expected" && range || (field.Key is "minimum" or "maximum") && !range ||
                        field.Key == "tolerance" && comparator is not ("ABS_TOL" or "REL_TOL")) continue;
                }
                var value = ParseScalar(field.Value.Trim());
                if (SequenceParameterValue.IsReference(value)) SequenceExpression.ValidateReferences(value.GetString()!, names);
            }
            foreach (var row in SignalValueRows)
            {
                var operation = SignalOperation(row); operation.Validate(IsReadSignalTableStep, names);
                if (!SignalChannelChoices.Contains(operation.ChannelId, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"通道不可用: {operation.ChannelId}");
                if (IsReadSignalTableStep && row.Value.Trim().Length > 0 && !variables.Contains(row.Value.Trim()))
                    throw new InvalidDataException($"接收变量未声明: {row.Value}");
            }
            foreach (var argument in CallArguments)
                if (argument.ReadValue() is { } value && SequenceParameterValue.IsReference(value)) SequenceExpression.ValidateReferences(value.GetString()!, names);
            ExpressionValidationMessage = "校验通过：语法、声明引用和常量类型有效。运行值、数组长度和计算结果将在执行时继续校验。";
        }
        catch (Exception error) when (IsEditorError(error)) { ExpressionValidationMessage = $"校验失败：{error.Message}"; }
        Status = ExpressionValidationMessage;
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ValidateVariable()
    {
        try { BuildVariable(SelectedVariable); Status = "变量校验通过：名称、作用域、类型和默认值有效"; }
        catch (Exception error) when (IsEditorError(error)) { Status = $"变量校验失败：{error.Message}"; }
    }

    private static Dictionary<string, JsonElement> DemoChannels() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sim.Voltage"] = JsonSerializer.SerializeToElement(4.8), ["Sim.Current"] = JsonSerializer.SerializeToElement(0.25),
        ["Sim.Temperature"] = JsonSerializer.SerializeToElement(25.0), ["Sim.Enabled"] = JsonSerializer.SerializeToElement(true),
        ["Sim.SerialNumber"] = JsonSerializer.SerializeToElement("SN001"), ["Sim.Samples"] = JsonSerializer.SerializeToElement(new[] { 4.8, 4.9, 5.0 })
    };

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void AddDemoChannels()
    {
        if (!CanEditSimulation || _workspace is null || HasPendingSimulationEdits && !TryApplySimulation()) return;
        try
        {
            var values = _workspaceService.LoadSimulation(_workspace).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in DemoChannels()) values.TryAdd(pair.Key, pair.Value);
            _workspaceService.SaveSimulation(_workspace, values); LoadSimulationValues(values); Status = "模拟通道已添加，保留原有同名通道值";
        }
        catch (Exception error) { Status = error.Message; }
    }
}
