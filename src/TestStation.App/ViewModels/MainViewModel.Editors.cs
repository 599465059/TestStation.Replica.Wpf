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
        if (!int.TryParse(StepTimeoutMs.Trim(), out var timeoutMs) || timeoutMs < 0)
            throw new InvalidDataException("超时必须是不小于 0 的整数毫秒数");
        if (!FailureActions.Contains(StepOnFailure) || !FailureActions.Contains(StepOnComplete))
            throw new InvalidDataException("失败动作无效");
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
                "scalar" => ParseScalar(value),
                "expression" => ParseExpression(value),
                _ => JsonSerializer.SerializeToElement(value)
            };
        }
        // Failure behavior has one source of truth in the common step properties.
        if (step.Kind == "assert") parameters.Remove("failure_action");
        if (step.Kind == "call_sequence") ApplyCallEditor(parameters);
        if (step.Kind is "set_value" or "read_value") ApplySignalTable(parameters, step.Kind);
        SequenceExpressionProgram.Parse(StepBeforeExpression);
        SequenceExpressionProgram.Parse(StepAfterExpression);
        return new SequenceStep { Name = name == UiDisplay.StepName(step) ? step.Name : name,
            Description = StepDescription, TimeoutMs = timeoutMs, OnFailure = StepOnFailure, OnComplete = StepOnComplete,
            FailureMessage = StepFailureMessage.Trim(), Precondition = StepPrecondition.Trim(),
            BeforeExpression = StepBeforeExpression, AfterExpression = StepAfterExpression,
            RecordResult = StepRecordResult, Params = parameters };
    }

    private static void ApplyStepUpdate(SequenceStep step, SequenceStep update)
    {
        step.Name = update.Name;
        step.Description = update.Description;
        step.TimeoutMs = update.TimeoutMs;
        step.OnFailure = update.OnFailure;
        step.OnComplete = update.OnComplete;
        step.FailureMessage = update.FailureMessage;
        step.Precondition = update.Precondition;
        step.BeforeExpression = update.BeforeExpression; step.AfterExpression = update.AfterExpression;
        step.RecordResult = update.RecordResult;
        step.Params = update.Params;
    }
    private static bool IsEditorError(Exception error) =>
        error is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException;

    partial void OnSelectedRowChanged(SequenceRow? oldValue, SequenceRow? newValue)
    {
        OnPropertyChanged(nameof(StepOutcomeLabel));
        OnPropertyChanged(nameof(StepOutcomeAction));
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
            StepTimeoutMs = value?.Step?.TimeoutMs.ToString(CultureInfo.InvariantCulture) ?? "0";
            StepOnFailure = value?.Step?.OnFailure is { Length: > 0 } action ? action : "STOP";
            StepOnComplete = value?.Step?.OnComplete is { Length: > 0 } complete ? complete : "CONTINUE";
            StepFailureMessage = value?.Step?.FailureMessage ?? "";
            StepPrecondition = value?.Step?.Precondition ?? "";
            StepBeforeExpression = value?.Step?.BeforeExpression ?? "";
            StepAfterExpression = value?.Step?.AfterExpression ?? "";
            StepRecordResult = value?.Step?.RecordResult ?? true;
            StepParameters = value?.Step is { } step ? JsonSerializer.Serialize(
                step.Params.Where(pair => !StepFieldCatalog.IsKnown(step.Kind, pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value), new JsonSerializerOptions { WriteIndented = true }) : "{}";
            StepFields.Clear();
            if (value?.Step is { } selected)
                foreach (var field in StepFieldCatalog.Create(selected.Kind, selected.Params).Where(_ => selected.Kind is not ("call_sequence" or "set_value" or "read_value")))
                { field.PropertyChanged += StepFieldChanged; StepFields.Add(field); }
            LoadCallEditor(value?.Step);
            LoadSignalTable(value?.Step);
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

    private static JsonElement ParseScalar(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Null)
                throw new InvalidDataException("设置值或比较值只能是标量、一维数组或 ${变量引用}");
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(v => v.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null))
                throw new InvalidDataException("只支持非空元素的一维数组");
            if (document.RootElement.ValueKind == JsonValueKind.Array && document.RootElement.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.Number && !double.IsFinite(v.GetDouble())))
                throw new InvalidDataException("数组中的数值必须是有限数字");
            if (document.RootElement.ValueKind == JsonValueKind.Number && !double.IsFinite(document.RootElement.GetDouble()))
                throw new InvalidDataException("数值必须是有限数字");
            if (SequenceParameterValue.IsReference(document.RootElement))
                SequenceParameterValue.ValidateReference(document.RootElement.GetString()!);
            return document.RootElement.Clone();
        }
        catch (JsonException error)
        {
            if (value.TrimStart().StartsWith('[')) throw new InvalidDataException("数组格式无效，请使用 JSON，例如 [1, 2, 3]", error);
            var text = JsonSerializer.SerializeToElement(value);
            if (SequenceParameterValue.IsReference(text)) SequenceParameterValue.ValidateReference(value);
            return text;
        }
    }

    private static JsonElement ParseExpression(string value)
    {
        SequenceExpression.ValidateSyntax(value);
        return JsonSerializer.SerializeToElement(value);
    }

    private void ReloadVariables()
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            Variables.Clear();
            foreach (var variable in _sequence?.Variables ?? []) Variables.Add(variable);
            foreach (var variable in _activeSubproject?.GlobalVariables ?? []) Variables.Add(variable);
            foreach (var variable in _stationVariables) Variables.Add(variable);
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
                StoreVariable(oldValue, variable);
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
    private void NewVariable(string? scope)
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            SelectedVariable = null; ClearVariableEditor();
            VariableScope = VariableScopes.Contains(scope ?? "") ? scope! : "local";
            var number = 1;
            while (VariableOwner(VariableScope).Any(v => v.Name.Equals($"变量{number}", StringComparison.OrdinalIgnoreCase))) number++;
            VariableName = $"变量{number}"; VariableDefault = "0";
            var variable = BuildVariable(null);
            StoreVariable(null, variable); ReloadVariables(); SelectedVariable = variable;
        }
        finally { _loadingEditors = loading; }
        NotifyDocumentState(); Status = "变量已新建，可在属性区或右键菜单重命名";
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
        if (VariableOwner(VariableScope).Any(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(item, previous)))
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
            StoreVariable(previous, variable);
            var loading = _loadingEditors; _loadingEditors = true;
            try { ReloadVariables(); SelectedVariable = variable; }
            finally { _loadingEditors = loading; }
            NotifyDocumentState(); Status = "变量已更新，保存序列后写入工程";
        }
        catch (Exception error) when (IsEditorError(error)) { Status = error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task DeleteVariable()
    {
        if (!CanEditSequence || SelectedVariable is not { } variable) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            if (!await _sequenceDialogs.ConfirmDeleteItemAsync("变量（引用该变量的步骤需相应调整）", variable.Name)) return;
            if (variable.Scope is "local" or "parameter") _document!.Edit(sequence => sequence.Variables.Remove(variable));
            else VariableOwner(variable.Scope).Remove(variable);
            ForgetRuntimeVariable(variable);
            ReloadVariables(); Status = "变量已删除，保存工程后写入";
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task RenameVariable()
    {
        if (!CanEditSequence || SelectedVariable is not { } variable || !TryCommitPendingSequenceEdits()) return;
        variable = SelectedVariable!;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            var name = await _sequenceDialogs.RequestNameAsync("重命名变量", variable.Name);
            if (name is null) return;
            VariableName = name;
            var update = BuildVariable(variable);
            StoreVariable(variable, update);
            ReloadVariables(); SelectedVariable = update;
            Status = "变量已重命名；请同步调整引用该名称的步骤";
        }
        catch (Exception error) when (IsEditorError(error)) { Status = error.Message; }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    private List<SequenceVariable> VariableOwner(string scope) => scope switch
    {
        "global" => _activeSubproject?.GlobalVariables ?? throw new InvalidOperationException("没有活动序列子工程"),
        "station" => _stationVariables,
        _ => _sequence?.Variables ?? throw new InvalidOperationException("没有活动序列")
    };

    private void StoreVariable(SequenceVariable? previous, SequenceVariable variable)
    {
        // Shared definitions live outside the sequence's local undo history.
        var previousLocal = previous is not null && (previous.Scope is "local" or "parameter");
        var nextLocal = variable.Scope is "local" or "parameter";
        if (previousLocal || nextLocal)
            _document!.Edit(sequence =>
            {
                var index = previousLocal ? sequence.Variables.IndexOf(previous!) : -1;
                if (previousLocal && nextLocal && index >= 0) sequence.Variables[index] = variable;
                else
                {
                    if (previousLocal) sequence.Variables.Remove(previous!);
                    if (nextLocal) sequence.Variables.Add(variable);
                }
            });
        if (previous is not null && !previousLocal) VariableOwner(previous.Scope).Remove(previous);
        if (!nextLocal) VariableOwner(variable.Scope).Add(variable);
        if (previous is not null && (previous.Name != variable.Name || previous.Scope != variable.Scope ||
            previous.DataType != variable.DataType || previous.Default?.GetRawText() != variable.Default?.GetRawText())) ForgetRuntimeVariable(previous);
        NotifyDocumentState();
    }

    private void ForgetRuntimeVariable(SequenceVariable variable)
    {
        if (variable.Scope == "station") _stationRuntime.Remove(variable.Name);
        if (variable.Scope == "global" && _activeSubproject is not null && _globalRuntime.TryGetValue(_activeSubproject.Id, out var values))
            values.Remove(variable.Name);
    }

    private static JsonElement? ParseVariableDefault(string text, string type, string scope)
    {
        if (type.EndsWith("[]", StringComparison.Ordinal))
            return text.Length == 0 && scope == "parameter" ? null :
                SequenceParameterValue.Parse(text.Length == 0 ? "[]" : text, type);
        if (text.Length == 0)
        {
            if (scope != "parameter")
                return type switch { "text" => JsonSerializer.SerializeToElement(""), "boolean" => JsonSerializer.SerializeToElement(false), _ => JsonSerializer.SerializeToElement(0) };
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
        _workspace is null ? new Dictionary<string, JsonElement>() : _workspaceService.LoadSimulation(_workspace));

    private void LoadSimulationValues(IReadOnlyDictionary<string, JsonElement> values)
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            SimulationValues.Clear();
            foreach (var pair in values.OrderBy(pair => pair.Key)) SimulationValues.Add(new SimulationValueRow(pair.Key, pair.Value));
            RefreshSignalChannels();
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
            var values = _workspaceService.LoadSimulation(_workspace)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (previous?.Channel != channel && values.ContainsKey(channel))
                throw new InvalidDataException($"模拟通道已存在: {channel}");
            if (previous is not null) values.Remove(previous.Channel);
            values[channel] = value;
            _workspaceService.SaveSimulation(_workspace, values);
            ReloadSimulationValues();
            SelectedSimulationValue = SimulationValues.First(item => item.Channel == channel);
            _simulationBaseline = CaptureSimulationEditor();
            ActiveSimulationScenario = "当前模拟数据（已修改）";
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
            var values = _workspaceService.LoadSimulation(_workspace!);
            values = values.Where(pair => !pair.Key.Equals(SelectedSimulationValue.Channel, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            _workspaceService.SaveSimulation(_workspace!, values); ReloadSimulationValues(); Status = "模拟通道已删除";
            ActiveSimulationScenario = "当前模拟数据（已修改）";
        }
        catch (Exception error) when (IsEditorError(error) || error is IOException or UnauthorizedAccessException) { Status = error.Message; }
    }

}
