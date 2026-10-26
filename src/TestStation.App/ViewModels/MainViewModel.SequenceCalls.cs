using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<SequenceDefinition> CallSequenceChoices { get; } = [];
    public ObservableCollection<ParameterInputRow> CallArguments { get; } = [];
    [ObservableProperty] private string _callSequenceId = "";
    [ObservableProperty] private string _callEditorMessage = "";
    public bool IsCallStep => SelectedRow?.Step?.Kind == "call_sequence";
    private bool _loadingCallEditor;
    private string _callSourceError = "";
    private Dictionary<string, JsonElement> _extraCallArguments = new(StringComparer.OrdinalIgnoreCase);

    private void LoadCallEditor(SequenceStep? step)
    {
        _loadingCallEditor = true;
        try
        {
            CallSequenceChoices.Clear();
            foreach (var sequence in Sequences.Where(s => !s.IsMain && s.Id != _sequence?.Id)) CallSequenceChoices.Add(sequence);
            CallSequenceId = step?.Params.TryGetValue("sequence_id", out var id) == true && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
            _callSourceError = "";
            var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            if (step?.Params.TryGetValue("arguments", out var value) == true)
            {
                if (value.ValueKind != JsonValueKind.Object) _callSourceError = "原调用参数不是对象，请重新选择目标序列并配置参数";
                else foreach (var pair in value.EnumerateObject())
                    if (!arguments.TryAdd(pair.Name, pair.Value.Clone())) _callSourceError = "原调用参数含重复名称，请重新选择目标序列并配置参数";
            }
            PopulateCallArguments(arguments);
            if (_callSourceError.Length > 0) CallEditorMessage = _callSourceError;
            OnPropertyChanged(nameof(IsCallStep));
        }
        finally { _loadingCallEditor = false; }
    }

    partial void OnCallSequenceIdChanged(string value)
    {
        if (_loadingCallEditor) return;
        _callSourceError = "";
        PopulateCallArguments(new Dictionary<string, JsonElement>());
        OnEditorChanged();
    }

    private void PopulateCallArguments(IReadOnlyDictionary<string, JsonElement> supplied)
    {
        foreach (var row in CallArguments) row.PropertyChanged -= StepFieldChanged;
        CallArguments.Clear();
        _extraCallArguments = new Dictionary<string, JsonElement>(supplied, StringComparer.OrdinalIgnoreCase);
        var target = Sequences.FirstOrDefault(s => s.Id.Equals(CallSequenceId, StringComparison.OrdinalIgnoreCase));
        if (target is null) { CallEditorMessage = string.IsNullOrEmpty(CallSequenceId) ? "请选择子序列" : $"目标子序列已不存在: {CallSequenceId}"; return; }
        var suggestions = (_sequence?.Variables.Select(v => "${" + v.Name + "}") ?? [])
            .Concat((_activeSubproject?.GlobalVariables ?? []).Select(v => "${Globals." + v.Name + "}"))
            .Concat(_stationVariables.Select(v => "${Station." + v.Name + "}"))
            .Concat(_sequence is null ? [] : SequenceEditor.Walk(_sequence).Where(s => s.Kind == "loop")
                .Select(s => s.Params.GetValueOrDefault("variable")).Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => "${" + v.GetString() + "}"))
            .Concat(SimulationValues.Select(v => "${" + v.Channel + "}")).Distinct().ToArray();
        foreach (var definition in target.Variables.Where(v => v.Scope == "parameter"))
        {
            var row = new ParameterInputRow(definition, true, supplied.TryGetValue(definition.Name, out var initial) ? initial : null, suggestions);
            row.PropertyChanged += StepFieldChanged;
            CallArguments.Add(row);
            _extraCallArguments.Remove(definition.Name);
        }
        CallEditorMessage = _extraCallArguments.Count > 0
            ? $"存在已失效的参数：{string.Join("、", _extraCallArguments.Keys)}。重新选择目标序列后配置参数。"
            : CallArguments.Count == 0 ? "此子序列没有运行参数" : "常量按目标参数类型保存；变量引用在运行时取值。";
    }

    private void ApplyCallEditor(Dictionary<string, JsonElement> parameters)
    {
        if (string.IsNullOrWhiteSpace(CallSequenceId)) throw new InvalidDataException("请选择目标子序列");
        if (_callSourceError.Length > 0) throw new InvalidDataException(_callSourceError);
        if (!Sequences.Any(s => !s.IsMain && s.Id != _sequence?.Id && s.Id.Equals(CallSequenceId, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"目标子序列不存在: {CallSequenceId}");
        if (_extraCallArguments.Count > 0) throw new InvalidDataException(CallEditorMessage);
        var arguments = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CallArguments) if (row.ReadValue() is { } value) arguments[row.Name] = value;
        parameters["sequence_id"] = JsonSerializer.SerializeToElement(CallSequenceId);
        parameters["arguments"] = JsonSerializer.SerializeToElement(arguments);
    }
}
