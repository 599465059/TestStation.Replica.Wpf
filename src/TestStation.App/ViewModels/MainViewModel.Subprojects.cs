using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<SequenceSubproject> SequenceSubprojects { get; } = [];
    [ObservableProperty] private SequenceSubproject? _selectedSubproject;
    private SequenceSubproject? _activeSubproject;
    private bool _changingSubproject;
    private string _workspaceBaseline = "[]";
    private string _stationBaseline = "";
    private readonly List<SequenceVariable> _stationVariables = [];
    private readonly Dictionary<string, string> _tabSelections = new();
    private readonly Dictionary<string, Dictionary<string, JsonElement>> _globalRuntime = new();
    private readonly Dictionary<string, JsonElement> _stationRuntime = new(StringComparer.OrdinalIgnoreCase);
    public string RunEntryLabel => _sequence?.IsMain == true ? "运行主序列" : "调试子序列";
    public string RunEntryDescription => _sequence?.IsMain == true ? "运行主序列并生成报表" : "单独调试子序列，不生成报表和运行历史";
    public bool CanCreateMainSequence => CanManageSequences && _activeSubproject?.Sequences.Count(s => s.IsMain) == 0;
    private bool HasWorkspaceChanges => JsonSerializer.Serialize(SequenceSubprojects) != _workspaceBaseline ||
        JsonSerializer.Serialize(_stationVariables) != _stationBaseline;

    partial void OnSelectedSubprojectChanged(SequenceSubproject? value)
    {
        if (_changingSubproject) return;
        if (_documentOperation || IsExecutionBusy || _sequence is not null && !TryCommitPendingSequenceEdits())
        {
            _changingSubproject = true;
            try { SelectedSubproject = _activeSubproject; }
            finally { _changingSubproject = false; }
            return;
        }
        ActivateSubproject(value);
    }

    private void ActivateSubproject(SequenceSubproject? project)
    {
        if (_activeSubproject is not null && _sequence is not null) _tabSelections[_activeSubproject.Id] = _sequence.Id;
        AttachSequence(null);
        _activeSubproject = project;
        _changingSubproject = true; _selectionSync = true;
        try
        {
            SelectedSubproject = project;
            Sequences.Clear();
            foreach (var sequence in project?.Sequences ?? []) Sequences.Add(sequence);
        }
        finally { _changingSubproject = false; _selectionSync = false; }
        var selected = project is not null && _tabSelections.TryGetValue(project.Id, out var id)
            ? Sequences.FirstOrDefault(s => s.Id == id) : null;
        AttachSequence(selected ?? Sequences.FirstOrDefault(s => s.IsMain) ?? Sequences.FirstOrDefault());
        ExecutionState = "IDLE"; ExecutionSummary = ""; RunLogs.Clear();
        ResetRunInspection(); LoadRunReports();
    }

    private bool SaveAutomaticWorkspace()
    {
        if (_workspace is null) return false;
        try
        {
            _workspaceService.Save(_workspace, SequenceSubprojects, _stationVariables);
            _workspaceBaseline = JsonSerializer.Serialize(SequenceSubprojects);
            _stationBaseline = JsonSerializer.Serialize(_stationVariables);
            foreach (var sequence in SequenceSubprojects.SelectMany(p => p.Sequences)) _documents.Track(sequence).MarkSaved();
            NotifyDocumentState();
            return true;
        }
        catch (Exception error) { Status = $"序列工作区保存失败: {error.Message}"; return false; }
    }

    [RelayCommand(CanExecute = nameof(CanManageSequences))]
    private void NewSubproject()
    {
        if (!CanManageSequences || _sequence is not null && !TryCommitPendingSequenceEdits()) return;
        var number = 1;
        while (SequenceSubprojects.Any(p => p.Name == $"序列子工程 {number}")) number++;
        var project = SequenceWorkspaceRepository.CreateSubproject($"序列子工程 {number}");
        SequenceSubprojects.Add(project);
        foreach (var sequence in project.Sequences) _documents.Track(sequence, saved: false);
        ActivateSubproject(project); Status = "已新建序列子工程，默认包含一个主序列";
    }

    [RelayCommand(CanExecute = nameof(CanManageSequences))]
    private async Task RenameSubproject()
    {
        if (_activeSubproject is null || !CanManageSequences) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            var name = await _sequenceDialogs.RequestNameAsync("重命名序列子工程", _activeSubproject.Name);
            if (name is null) return;
            name = name.Trim();
            if (name.Length == 0 || SequenceSubprojects.Any(p => p != _activeSubproject && p.Name == name))
            { Status = "子工程名称不能为空或重复"; return; }
            _activeSubproject.Name = name;
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    [RelayCommand(CanExecute = nameof(CanManageSequences))]
    private async Task DeleteSubproject()
    {
        if (_activeSubproject is null || !CanManageSequences) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            if (!await _sequenceDialogs.ConfirmDeleteItemAsync("序列子工程及其全部序列", _activeSubproject.Name)) return;
            var project = _activeSubproject;
            ActivateSubproject(null);
            SequenceSubprojects.Remove(project);
            foreach (var sequence in project.Sequences) _documents.Forget(sequence);
            _globalRuntime.Remove(project.Id); _tabSelections.Remove(project.Id);
            ActivateSubproject(SequenceSubprojects.FirstOrDefault()); Status = "序列子工程已删除，保存工程后生效";
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    // Kept for compatibility with older bindings. A valid sequence subproject
    // already has its one main sequence, so this is disabled in the normal UI.
    [RelayCommand(CanExecute = nameof(CanCreateMainSequence))]
    private void NewMainSequence()
    {
        if (_activeSubproject is null || !CanCreateMainSequence || _sequence is not null && !TryCommitPendingSequenceEdits()) return;
        var sequence = SequenceWorkspaceRepository.NewMain();
        sequence.Name = UniqueSequenceName(sequence.Name);
        _documents.Track(sequence, saved: false);
        _activeSubproject.Sequences.Add(sequence); Sequences.Add(sequence); AttachSequence(sequence);
    }

    private string UniqueSequenceName(string name)
    {
        var unique = name;
        for (var suffix = 2; Sequences.Any(s => s.Name == unique); suffix++) unique = $"{name} {suffix}";
        return unique;
    }

    private sealed record SequenceClipboard(string EntryId, List<SequenceDefinition> Sequences, List<SequenceVariable> Globals);
    private SequenceClipboard? _sequenceClipboard;

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void CopySubsequence()
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits() || _sequence is null || _activeSubproject is null) return;
        if (_sequence.IsMain) { Status = "请选中子序列后复制"; return; }
        var copies = new Dictionary<string, SequenceDefinition>(StringComparer.OrdinalIgnoreCase);
        void Collect(SequenceDefinition source)
        {
            if (!copies.TryAdd(source.Id, SequenceDocumentSession.Clone(source))) return;
            foreach (var step in SequenceEditor.Walk(source).Where(s => s.Kind == "call_sequence"))
            {
                var id = step.Params.GetValueOrDefault("sequence_id").ToString();
                var target = Sequences.FirstOrDefault(s => s.Id == id);
                if (target is null || target.IsMain) throw new InvalidOperationException("存在无效的子序列调用，请修正后复制");
                Collect(target);
            }
        }
        try
        {
            Collect(_sequence);
            _sequenceClipboard = new(_sequence.Id, copies.Values.ToList(),
                JsonSerializer.Deserialize<List<SequenceVariable>>(JsonSerializer.Serialize(_activeSubproject.GlobalVariables))!);
            Status = $"已复制子序列及依赖（{copies.Count} 个），可切换标签页后粘贴";
        }
        catch (Exception error) { Status = error.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanManageSequences))]
    private void PasteSubsequence()
    {
        if (!CanManageSequences || _activeSubproject is null || _sequenceClipboard is null ||
            _sequence is not null && !TryCommitPendingSequenceEdits()) return;
        var clipboard = _sequenceClipboard;
        // Resolve global definitions before changing anything; never silently overwrite another tab's values.
        foreach (var variable in clipboard.Globals)
        {
            var existing = _activeSubproject.GlobalVariables.FirstOrDefault(v => v.Name.Equals(variable.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (existing.DataType != variable.DataType || existing.Default?.GetRawText() != variable.Default?.GetRawText()))
            { Status = $"全局变量 {variable.Name} 与目标标签页定义冲突，请先统一名称、类型和默认值"; return; }
        }
        var map = clipboard.Sequences.ToDictionary(s => s.Id, _ => Guid.NewGuid().ToString("N"), StringComparer.OrdinalIgnoreCase);
        foreach (var source in clipboard.Sequences)
        {
            var copy = SequenceDocumentSession.Clone(source); copy.Id = map[source.Id]; copy.Role = "sub";
            copy.Name = UniqueSequenceName(source.Name);
            foreach (var step in SequenceEditor.Walk(copy))
            {
                step.Id = Guid.NewGuid().ToString("N");
                if (step.Kind == "call_sequence" && step.Params.TryGetValue("sequence_id", out var id) && map.TryGetValue(id.ToString(), out var mapped))
                    step.Params["sequence_id"] = JsonSerializer.SerializeToElement(mapped);
            }
            _documents.Track(copy, saved: false); _activeSubproject.Sequences.Add(copy); Sequences.Add(copy);
        }
        foreach (var variable in clipboard.Globals)
            if (!_activeSubproject.GlobalVariables.Any(v => v.Name.Equals(variable.Name, StringComparison.OrdinalIgnoreCase)))
                _activeSubproject.GlobalVariables.Add(JsonSerializer.Deserialize<SequenceVariable>(JsonSerializer.Serialize(variable))!);
        AttachSequence(Sequences.First(s => s.Id == map[clipboard.EntryId]));
        Status = "已粘贴子序列及依赖，内部调用已重新关联";
    }
}
