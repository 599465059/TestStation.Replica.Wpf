using System.IO;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Domain;
using TestStation.App.Localization;
using TestStation.App.Services;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    private readonly ISequenceDocumentService _documents;
    private readonly ISequenceDialogService _sequenceDialogs;
    private SequenceDocumentSession? _document;
    private bool _loadingEditors;
    private bool _selectionSync;
    private bool _documentOperation;
    private bool _runInProgress;
    private Task? _runTask;
    private TaskCompletionSource? _runStarted;
    private string _stepBaseline = "";
    private string _variableBaseline = "";
    private string _simulationBaseline = "";
    public bool CanEditSequence => _sequence is not null && !IsExecutionBusy && !_documentOperation;
    public bool CanManageSequences => IsProjectOpen && !IsExecutionBusy && !_documentOperation;
    public bool CanEditSimulation => IsProjectOpen && !IsExecutionBusy && !_documentOperation;
    public bool CanUndoSequence => CanEditSequence && (_document?.CanUndo == true || HasPendingSequenceEdits);
    public bool CanRedoSequence => CanEditSequence && _document?.CanRedo == true && !HasPendingSequenceEdits;
    public bool IsSequenceDirty => _document?.IsDirty == true || HasPendingSequenceEdits;
    public string SequenceDocumentStatus => _sequence is null ? "" : IsSequenceDirty ? UiText.Unsaved : UiText.Saved;
    public string SequenceDirtyMarker => IsSequenceDirty ? " *" : "";
    public string SequenceEditorTitle => UiText.SequenceEditor + SequenceDirtyMarker;
    private bool HasPendingStepEdits => SelectedRow?.Step is not null && CaptureStepEditor() != _stepBaseline;
    private bool HasPendingVariableEdits => _sequence is not null && CaptureVariableEditor() != _variableBaseline;
    private bool HasPendingSimulationEdits => _workspace is not null && CaptureSimulationEditor() != _simulationBaseline;
    private bool HasPendingSequenceEdits => _sequence is not null &&
        (SequenceName != _sequence.Name || SequenceDescription != _sequence.Description || HasPendingStepEdits || HasPendingVariableEdits);

    private string CaptureStepEditor() => JsonSerializer.Serialize(new { StepName, StepDescription, StepParameters,
        Fields = StepFields.Select(field => new { field.Key, field.Value }).ToArray() });
    private string CaptureVariableEditor() => JsonSerializer.Serialize(new { VariableName, VariableType, VariableScope, VariableDefault });
    private string CaptureSimulationEditor() => JsonSerializer.Serialize(new { SimulationChannel, SimulationValue });
    private void CaptureEditorBaselines()
    {
        _stepBaseline = CaptureStepEditor(); _variableBaseline = CaptureVariableEditor();
    }
    private void OnEditorChanged() { if (!_loadingEditors) NotifyDocumentState(); }
    partial void OnSequenceNameChanged(string value) => OnEditorChanged();
    partial void OnSequenceDescriptionChanged(string value) => OnEditorChanged();
    partial void OnStepNameChanged(string value) => OnEditorChanged();
    partial void OnStepDescriptionChanged(string value) => OnEditorChanged();
    partial void OnStepParametersChanged(string value) => OnEditorChanged();
    partial void OnVariableNameChanged(string value) => OnEditorChanged();
    partial void OnVariableTypeChanged(string value) => OnEditorChanged();
    partial void OnVariableScopeChanged(string value) => OnEditorChanged();
    partial void OnVariableDefaultChanged(string value) => OnEditorChanged();
    private void StepFieldChanged(object? sender, PropertyChangedEventArgs args) => OnEditorChanged();

    private void NotifyDocumentState()
    {
        foreach (var property in new[] { nameof(CanEditSequence), nameof(CanManageSequences), nameof(CanEditSimulation),
            nameof(CanRunSequence), nameof(CanUndoSequence), nameof(CanRedoSequence), nameof(IsSequenceDirty),
            nameof(SequenceDocumentStatus), nameof(SequenceDirtyMarker), nameof(SequenceEditorTitle), nameof(IsExecutionBusy) }) OnPropertyChanged(property);
        foreach (var row in Rows) row.CanEdit = CanEditSequence;
        OnPropertyChanged(nameof(CanSaveProject));
        SaveProjectCommand.NotifyCanExecuteChanged();
        NewSequenceCommand.NotifyCanExecuteChanged(); SaveSequenceCommand.NotifyCanExecuteChanged();
        RenameSequenceCommand.NotifyCanExecuteChanged(); DuplicateSequenceCommand.NotifyCanExecuteChanged(); DeleteSequenceCommand.NotifyCanExecuteChanged();
        UndoSequenceCommand.NotifyCanExecuteChanged(); RedoSequenceCommand.NotifyCanExecuteChanged();
        DeleteStepCommand.NotifyCanExecuteChanged(); ApplyStepCommand.NotifyCanExecuteChanged();
        NewVariableCommand.NotifyCanExecuteChanged(); ApplyVariableCommand.NotifyCanExecuteChanged(); DeleteVariableCommand.NotifyCanExecuteChanged();
        NewSimulationValueCommand.NotifyCanExecuteChanged(); ApplySimulationValueCommand.NotifyCanExecuteChanged(); DeleteSimulationValueCommand.NotifyCanExecuteChanged();
        RunSequenceCommand.NotifyCanExecuteChanged(); PauseResumeCommand.NotifyCanExecuteChanged(); StopSequenceCommand.NotifyCanExecuteChanged();
    }

    private void AttachSequence(SequenceDefinition? sequence)
    {
        if (_document is not null) _document.Changed -= NotifyDocumentState;
        _sequence = sequence;
        _document = sequence is null ? null : _documents.Track(sequence);
        if (_document is not null) _document.Changed += NotifyDocumentState;
        _selectionSync = true;
        try { SelectedSequence = sequence; }
        finally { _selectionSync = false; }
        _collapsed.Clear(); _stepStatuses.Clear();
        RefreshDocumentEditors();
    }

    private void RefreshDocumentEditors(string? selectedStepId = null)
    {
        _loadingEditors = true;
        try
        {
            SequenceName = _sequence?.Name ?? ""; SequenceDescription = _sequence?.Description ?? "";
            SelectedRow = null; RefreshRows(); ReloadVariables();
            SelectedRow = Rows.FirstOrDefault(row => row.Step?.Id == selectedStepId && row.Step is not null)
                ?? Rows.FirstOrDefault(row => row.Step is not null);
            CaptureEditorBaselines();
            if (_sequence is not null && Sequences.IndexOf(_sequence) is var index && index >= 0) Sequences[index] = _sequence;
            _selectionSync = true;
            try { SelectedSequence = _sequence; }
            finally { _selectionSync = false; }
        }
        finally { _loadingEditors = false; }
        NotifyDocumentState();
    }

    private void RevertSequenceSelection()
    {
        _selectionSync = true;
        try { SelectedSequence = _sequence; }
        finally { _selectionSync = false; }
    }
    public async Task<bool> SelectSequenceAsync(SequenceDefinition? sequence)
    {
        if (ReferenceEquals(sequence, _sequence)) return true;
        if (_documentOperation || IsExecutionBusy) { RevertSequenceSelection(); return false; }
        _documentOperation = true; NotifyDocumentState();
        try
        {
            if (!await ResolveUnsavedSequenceAsync()) return false;
            AttachSequence(sequence); return true;
        }
        catch (Exception error) { Status = error.Message; return false; }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    private async Task<bool> ResolveUnsavedSequenceAsync()
    {
        if (!IsSequenceDirty) return true;
        var choice = await _sequenceDialogs.AskUnsavedSequenceAsync(SequenceName);
        if (choice == SequenceUnsavedChoice.Cancel) return false;
        if (choice == SequenceUnsavedChoice.Save) return TrySaveSequence();
        if (_document?.Discard() != true && _sequence is { } unsaved)
        {
            _documents.Forget(unsaved);
            _selectionSync = true;
            try { Sequences.Remove(unsaved); }
            finally { _selectionSync = false; }
            AttachSequence(null);
        }
        else RefreshDocumentEditors();
        return true;
    }

    public async Task<bool> PrepareToLeaveAsync()
    {
        if (_documentOperation) return false;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            if (IsExecutionBusy)
            {
                if (!await _sequenceDialogs.ConfirmStopExecutionAsync()) return false;
                await StopAndWaitAsync();
            }
            if (!await ResolveUnsavedSequenceAsync()) return false;
            if (HasPendingSimulationEdits)
            {
                var choice = await _sequenceDialogs.AskUnsavedSequenceAsync(UiText.Simulation);
                if (choice == SequenceUnsavedChoice.Cancel) return false;
                if (choice == SequenceUnsavedChoice.Save && !TryApplySimulation()) return false;
                if (choice == SequenceUnsavedChoice.Discard) ReloadSimulationValues();
            }
            return true;
        }
        catch (Exception error) { Status = error.Message; return false; }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    private bool TryCommitPendingSequenceEdits()
    {
        if (_sequence is null || IsExecutionBusy) return false;
        try
        {
            var name = SequenceName.Trim();
            if (name.Length == 0) throw new InvalidDataException("序列名称不能为空");
            var step = HasPendingStepEdits ? SelectedRow?.Step : null;
            var update = step is null ? null : BuildStepUpdate(step);
            var previous = SelectedVariable;
            var variable = HasPendingVariableEdits ? BuildVariable(previous) : null;
            _document!.Edit(sequence =>
            {
                sequence.Name = name; sequence.Description = SequenceDescription;
                if (step is not null && update is not null) ApplyStepUpdate(step, update);
                if (variable is not null)
                {
                    if (previous is null) sequence.Variables.Add(variable);
                    else sequence.Variables[sequence.Variables.IndexOf(previous)] = variable;
                }
            });
            _loadingEditors = true;
            try { SequenceName = name; if (variable is not null) { ReloadVariables(); SelectedVariable = variable; } CaptureEditorBaselines(); }
            finally { _loadingEditors = false; }
            NotifyDocumentState(); return true;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException)
        { Status = error.Message; return false; }
    }

    private bool TrySaveSequence()
    {
        if (_workspace is null || _sequence is null || IsExecutionBusy || !TryCommitPendingSequenceEdits()) return false;
        try { _workspace.SaveSequence(_sequence); _document!.MarkSaved(); Status = $"已保存: {_sequence.Name}"; return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Status = error.Message; return false; }
    }

    [RelayCommand(CanExecute = nameof(CanUndoSequence))]
    private void UndoSequence()
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        var id = SelectedRow?.Step?.Id;
        if (_document!.Undo()) { RefreshDocumentEditors(id); Status = UiText.Undone; }
    }
    [RelayCommand(CanExecute = nameof(CanRedoSequence))]
    private void RedoSequence()
    {
        if (!CanRedoSequence) return;
        var id = SelectedRow?.Step?.Id;
        if (_document!.Redo()) { RefreshDocumentEditors(id); Status = UiText.Redone; }
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task RenameSequence()
    {
        if (!CanEditSequence) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            var name = await _sequenceDialogs.RenameSequenceAsync(SequenceName);
            if (name is null) return;
            if (string.IsNullOrWhiteSpace(name)) { Status = "序列名称不能为空"; return; }
            if (!TryCommitPendingSequenceEdits()) return;
            _document!.Edit(sequence => sequence.Name = name.Trim()); RefreshDocumentEditors(SelectedRow?.Step?.Id);
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }
    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task DuplicateSequence()
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            var source = _sequence!;
            if (!await ResolveUnsavedSequenceAsync()) return;
            var copy = _documents.Duplicate(source, Sequences.Select(item => item.Id));
            copy.Name = UiText.Format(nameof(UiText.SequenceCopyName), source.Name);
            _documents.Track(copy, saved: false); Sequences.Add(copy); AttachSequence(copy);
            Status = UiText.SequenceCopied;
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }
    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private async Task DeleteSequence()
    {
        if (!CanEditSequence) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            var sequence = _sequence!;
            var caller = Sequences.FirstOrDefault(item => item != sequence && SequenceEditor.Walk(item)
                .Any(step => step.Kind == "call_sequence" && step.Params.TryGetValue("sequence_id", out var id) && id.ToString() == sequence.Id));
            if (caller is not null) { Status = UiText.Format(nameof(UiText.SequenceInUse), caller.Name); return; }
            if (!await _sequenceDialogs.ConfirmDeleteSequenceAsync(SequenceName)) return;
            _workspace!.DeleteSequence(sequence.Id);
            _documents.Forget(sequence);
            _selectionSync = true;
            try { Sequences.Remove(sequence); }
            finally { _selectionSync = false; }
            AttachSequence(Sequences.FirstOrDefault()); Status = UiText.SequenceDeleted;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Status = error.Message; }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    public bool SetStepEnabled(SequenceStep step, bool enabled)
    {
        if (!CanEditSequence || !SequenceEditor.Walk(_sequence!).Contains(step)) return false;
        _document!.Edit(_ => step.Enabled = enabled); NotifyDocumentState(); return true;
    }
    private async Task StopAndWaitAsync()
    {
        if (_runInProgress && _runStarted is { } started) await started.Task;
        await _executionEngine.StopAsync();
        if (_runTask is { } task) await task;
        if (RunSequenceCommand.ExecutionTask is { } completion) await completion;
    }
    private void EnsureWorkspaceCanChange()
    {
        if (IsExecutionBusy || IsSequenceDirty || HasPendingSimulationEdits)
            throw new InvalidOperationException(UiText.ResolveChangesFirst);
    }
}
