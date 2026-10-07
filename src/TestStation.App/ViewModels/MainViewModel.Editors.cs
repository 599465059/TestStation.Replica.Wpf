using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ApplyStep()
    {
        if (!CanEditSequence || SelectedRow?.Step is not { } step) return;
        try
        {
            var update = BuildStepUpdate(step);
            if (!TryCommitPendingSequenceEdits()) return;
            _document!.Edit(_ => ApplyStepUpdate(step, update));
            RefreshDocumentEditors(step.Id);
            Status = "步骤属性已更新，保存后写入工程";
        }
        catch (Exception error) when (IsEditorError(error)) { Status = error.Message; }
    }

    private SequenceStep BuildStepUpdate(SequenceStep step)
    {
        var name = StepName.Trim();
        if (name.Length == 0) throw new InvalidDataException("步骤名称不能为空");
        using var document = JsonDocument.Parse(StepParameters);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("参数必须是 JSON 对象");
        var parameters = document.RootElement.EnumerateObject().ToDictionary(pair => pair.Name, pair => pair.Value.Clone());
        foreach (var field in StepFields)
        {
            var value = field.Value.Trim();
            if (value.Length == 0) { parameters.Remove(field.Key); continue; }
            parameters[field.Key] = field.FieldType switch
            {
                "integer" when int.TryParse(value, out var number) && number >= 0 => JsonSerializer.SerializeToElement(number),
                "integer" => throw new InvalidDataException($"{field.Label} 必须是不小于 0 的整数"),
                "json" => ParseObject(value, field.Label),
                _ => JsonSerializer.SerializeToElement(value)
            };
        }
        return new SequenceStep { Name = name == UiDisplay.StepName(step) ? step.Name : name,
            Description = StepDescription, Params = parameters };
    }

    private static void ApplyStepUpdate(SequenceStep step, SequenceStep update)
    { step.Name = update.Name; step.Description = update.Description; step.Params = update.Params; }
    private static bool IsEditorError(Exception error) =>
        error is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException;

    partial void OnSelectedRowChanged(SequenceRow? oldValue, SequenceRow? newValue)
    {
        var value = newValue;
        if (!_loadingEditors && oldValue?.Step is { } previous && CaptureStepEditor() != _stepBaseline)
        {
            try
            {
                var update = BuildStepUpdate(previous);
                if (!CanEditSequence) throw new InvalidOperationException(UiText.ResolveChangesFirst);
                _document!.Edit(_ => ApplyStepUpdate(previous, update));
            }
            catch (Exception error) when (IsEditorError(error) || error is InvalidOperationException)
            {
                SetProperty(ref _selectedRow, oldValue, nameof(SelectedRow));
                Status = error.Message; return;
            }
        }
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            foreach (var field in StepFields) field.PropertyChanged -= StepFieldChanged;
            StepName = value?.Step is { } selectedStep ? UiDisplay.StepName(selectedStep) : "";
            StepDescription = value?.Step?.Description ?? "";
            StepParameters = value?.Step is { } step ? JsonSerializer.Serialize(
                step.Params.Where(pair => !StepFieldCatalog.IsKnown(step.Kind, pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value), new JsonSerializerOptions { WriteIndented = true }) : "{}";
            StepFields.Clear();
            if (value?.Step is { } selected)
                foreach (var field in StepFieldCatalog.Create(selected.Kind, selected.Params))
                { field.PropertyChanged += StepFieldChanged; StepFields.Add(field); }
            _stepBaseline = CaptureStepEditor();
        }
        finally { _loadingEditors = loading; }
        NotifyDocumentState();
    }

    private static JsonElement ParseObject(string value, string label)
    {
        using var document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} 必须是 JSON 对象");
        return document.RootElement.Clone();
    }

    private void ReloadVariables()
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            Variables.Clear();
            foreach (var variable in _sequence?.Variables ?? []) Variables.Add(variable);
            SelectedVariable = null; ClearVariableEditor();
        }
        finally { _loadingEditors = loading; }
    }

    partial void OnSelectedVariableChanged(SequenceVariable? oldValue, SequenceVariable? newValue)
    {
        var value = newValue;
        if (!_loadingEditors && CaptureVariableEditor() != _variableBaseline)
        {
            try
            {
                if (!CanEditSequence) throw new InvalidOperationException(UiText.ResolveChangesFirst);
                var variable = BuildVariable(oldValue);
                _document!.Edit(sequence => {
                    if (oldValue is null) sequence.Variables.Add(variable);
                    else sequence.Variables[sequence.Variables.IndexOf(oldValue)] = variable;
                });
                if (oldValue is null) Variables.Add(variable);
                else Variables[Variables.IndexOf(oldValue)] = variable;
            }
            catch (Exception error) when (IsEditorError(error) || error is InvalidOperationException)
            {
                SetProperty(ref _selectedVariable, oldValue, nameof(SelectedVariable));
                Status = error.Message; return;
            }
        }
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            if (value is null) ClearVariableEditor();
            else
            {
                VariableName = value.Name; VariableType = value.DataType; VariableScope = value.Scope;
                VariableDefault = value.Default is { } item
                    ? item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.GetRawText() : "";
                _variableBaseline = CaptureVariableEditor();
            }
        }
        finally { _loadingEditors = loading; }
        NotifyDocumentState();
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void NewVariable()
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        SelectedVariable = null; ClearVariableEditor(); NotifyDocumentState();
    }

    private void ClearVariableEditor()
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try { VariableName = ""; VariableType = "number"; VariableScope = "local"; VariableDefault = "";
            _variableBaseline = CaptureVariableEditor(); }
        finally { _loadingEditors = loading; }
    }

    private SequenceVariable BuildVariable(SequenceVariable? previous)
    {
        var name = VariableName.Trim();
        if (!Regex.IsMatch(name, @"^[_\p{L}][_\p{L}\p{N}]*$")) throw new InvalidDataException("变量名称必须是有效标识符");
        if (_sequence!.Variables.Any(item => item.Name == name && !ReferenceEquals(item, previous)))
            throw new InvalidDataException($"变量已存在: {name}");
        if (!VariableTypes.Contains(VariableType) || !VariableScopes.Contains(VariableScope))
            throw new InvalidDataException("变量类型或作用域无效");
        return new SequenceVariable { Name = name, DataType = VariableType, Scope = VariableScope,
            Default = ParseVariableDefault(VariableDefault.Trim(), VariableType, VariableScope) };
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void ApplyVariable()
    {
        if (!CanEditSequence) return;
        try
        {
            var previous = SelectedVariable;
            var variable = BuildVariable(previous);
            _document!.Edit(sequence => {
                if (previous is null) sequence.Variables.Add(variable);
                else sequence.Variables[sequence.Variables.IndexOf(previous)] = variable;
            });
            var loading = _loadingEditors; _loadingEditors = true;
            try { ReloadVariables(); SelectedVariable = variable; }
            finally { _loadingEditors = loading; }
            NotifyDocumentState(); Status = "变量已更新，保存序列后写入工程";
        }
        catch (Exception error) when (IsEditorError(error)) { Status = error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteVariable()
    {
        if (!CanEditSequence || SelectedVariable is not { } variable) return;
        _document!.Edit(sequence => sequence.Variables.Remove(variable));
        ReloadVariables(); NotifyDocumentState();
        Status = "变量已删除，保存序列后写入工程";
    }

    private static JsonElement? ParseVariableDefault(string text, string type, string scope)
    {
        if (text.Length == 0)
        {
            if (scope == "local") throw new InvalidDataException("局部变量必须有默认值");
            return null;
        }
        return type switch
        {
            "text" => JsonSerializer.SerializeToElement(text),
            "boolean" when text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" => JsonSerializer.SerializeToElement(true),
            "boolean" when text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" => JsonSerializer.SerializeToElement(false),
            "boolean" => throw new InvalidDataException("布尔变量只能是 true 或 false"),
            "integer" when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => JsonSerializer.SerializeToElement(integer),
            "integer" => throw new InvalidDataException("整数变量默认值无效"),
            "number" when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) => JsonSerializer.SerializeToElement(number),
            "number" => throw new InvalidDataException("数值变量默认值无效"),
            _ => throw new InvalidDataException("变量类型无效")
        };
    }

    private void ReloadSimulationValues() => LoadSimulationValues(
        _workspace is null ? new Dictionary<string, JsonElement>() : new SimulationRepository(_workspace.Root).Load());

    private void LoadSimulationValues(IReadOnlyDictionary<string, JsonElement> values)
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            SimulationValues.Clear();
            foreach (var pair in values.OrderBy(pair => pair.Key)) SimulationValues.Add(new SimulationValueRow(pair.Key, pair.Value));
            SelectedSimulationValue = null; SimulationChannel = ""; SimulationValue = "";
            _simulationBaseline = CaptureSimulationEditor();
        }
        finally { _loadingEditors = loading; }
    }

    partial void OnSelectedSimulationValueChanged(SimulationValueRow? oldValue, SimulationValueRow? newValue)
    {
        var value = newValue;
        if (!_loadingEditors && HasPendingSimulationEdits)
        {
            // Resolve pending form against the old channel, then load the new row by ID.
            if (!CanEditSimulation || !TryApplySimulation(oldValue))
            { SetProperty(ref _selectedSimulationValue, oldValue, nameof(SelectedSimulationValue)); return; }
            value = SimulationValues.FirstOrDefault(item => item.Channel == value?.Channel);
            SetProperty(ref _selectedSimulationValue, value, nameof(SelectedSimulationValue));
        }
        SimulationChannel = value?.Channel ?? ""; SimulationValue = value?.DisplayValue ?? "";
        _simulationBaseline = CaptureSimulationEditor();
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void NewSimulationValue()
    {
        if (!CanEditSimulation || HasPendingSimulationEdits && !TryApplySimulation()) return;
        SelectedSimulationValue = null; SimulationChannel = ""; SimulationValue = "";
        _simulationBaseline = CaptureSimulationEditor();
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void ApplySimulationValue() { if (CanEditSimulation) TryApplySimulation(); }
    private bool TryApplySimulation() => TryApplySimulation(SelectedSimulationValue);
    private bool TryApplySimulation(SimulationValueRow? previous)
    {
        if (_workspace is null || IsExecutionBusy) return false;
        try
        {
            var channel = SimulationChannel.Trim();
            if (channel.Length == 0) throw new InvalidDataException("模拟通道 ID 不能为空");
            using var document = JsonDocument.Parse(SimulationValue);
            var value = document.RootElement.Clone();
            var repository = new SimulationRepository(_workspace.Root);
            var values = repository.Load();
            if (previous?.Channel != channel && values.ContainsKey(channel))
                throw new InvalidDataException($"模拟通道已存在: {channel}");
            if (previous is not null) values.Remove(previous.Channel);
            values[channel] = value;
            repository.Save(values);
            ReloadSimulationValues();
            SelectedSimulationValue = SimulationValues.First(item => item.Channel == channel);
            _simulationBaseline = CaptureSimulationEditor();
            Status = $"模拟通道已保存: {channel}"; return true;
        }
        catch (Exception error) when (IsEditorError(error) || error is IOException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSimulation))]
    private void DeleteSimulationValue()
    {
        if (!CanEditSimulation || SelectedSimulationValue is null) return;
        try
        {
            var repository = new SimulationRepository(_workspace!.Root);
            var values = repository.Load(); values.Remove(SelectedSimulationValue.Channel);
            repository.Save(values); ReloadSimulationValues(); Status = "模拟通道已删除";
        }
        catch (Exception error) when (IsEditorError(error) || error is IOException or UnauthorizedAccessException) { Status = error.Message; }
    }

}
