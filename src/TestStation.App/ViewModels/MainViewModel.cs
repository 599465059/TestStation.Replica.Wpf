using System.IO;
using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Domain;
using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

public partial class MainViewModel : ObservableObject, IProjectSaveParticipant
{
    private readonly ISequenceExecutionHost _executionHost;
    private readonly ISequenceRunPersistence _runPersistence;
    private readonly IAutomaticWorkspaceService _workspaceService;
    private readonly ISequenceReportService _reportService;
    private readonly IAutoTestDisplayService _displayService;
    private readonly ISequenceEditingService _editingService;
    private readonly ProjectSaveService _projectSaving = new();
    public string SaveSection => "自动测试与模拟数据";
    public bool CanSaveProject => IsProjectOpen && !IsExecutionBusy && !_documentOperation;
    public void RegisterProjectSaveParticipant(IProjectSaveParticipant participant) => _projectSaving.Register(participant);
    private IReadOnlyDictionary<string, SequenceDefinition> _runSequences = new Dictionary<string, SequenceDefinition>();
    private ProjectWorkspace? _workspace;
    private SequenceDefinition? _sequence;
    private readonly HashSet<string> _collapsed = [];
    private readonly Dictionary<string, string> _stepStatuses = new(StringComparer.Ordinal);
    [ObservableProperty] private bool _isProjectOpen;
    [ObservableProperty] private bool _isDarkTheme = true;
    [ObservableProperty] private string _projectName = "Test Station";
    [ObservableProperty] private string _currentPage = "自动测试";
    [ObservableProperty] private string _status = "尚未打开工程";
    [ObservableProperty] private SequenceRow? _selectedRow;
    [ObservableProperty] private SequenceDefinition? _selectedSequence;
    [ObservableProperty] private string _stepName = "";
    [ObservableProperty] private string _stepDescription = "";
    [ObservableProperty] private string _stepTimeoutMs = "0";
    [ObservableProperty] private string _stepOnFailure = "STOP";
    [ObservableProperty] private string _stepOnComplete = "CONTINUE";
    [ObservableProperty] private string _stepFailureMessage = "";
    [ObservableProperty] private string _stepPrecondition = "";
    [ObservableProperty] private bool _stepRecordResult = true;
    [ObservableProperty] private string _stepParameters = "{}";
    [ObservableProperty] private string _stepBeforeExpression = "";
    [ObservableProperty] private string _stepAfterExpression = "";
    [ObservableProperty] private string _sequenceName = "";
    [ObservableProperty] private string _sequenceDescription = "";
    [ObservableProperty] private SequenceVariable? _selectedVariable;
    [ObservableProperty] private string _variableName = "";
    [ObservableProperty] private string _variableType = "number";
    [ObservableProperty] private string _variableScope = "local";
    [ObservableProperty] private string _variableDefault = "";
    [ObservableProperty] private SimulationValueRow? _selectedSimulationValue;
    [ObservableProperty] private string _simulationChannel = "";
    [ObservableProperty] private string _simulationValue = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PauseResumeText)), NotifyPropertyChangedFor(nameof(PauseResumeIcon))] private string _executionState = "IDLE";
    [ObservableProperty] private string _executionSummary = "";

    public ObservableCollection<SequenceDefinition> Sequences { get; } = [];
    public ObservableCollection<SequenceRow> Rows { get; } = [];
    public ObservableCollection<StepParameterField> StepFields { get; } = [];
    public ObservableCollection<SignalValueRow> SignalValueRows { get; } = [];
    public bool IsSignalTableStep => SelectedRow?.Step?.Kind is "set_value" or "read_value";
    public IReadOnlyList<string> SignalDataTypes => SequenceValueOperations.DataTypes;
    public IReadOnlyList<string> SignalInputModes { get; } = ["constant", "expression"];
    public IReadOnlyList<string> SignalComparators => SequenceValueOperations.Comparators;
    public IReadOnlyList<string> SignalArrayModes => SequenceValueOperations.ArrayModes;
    public ObservableCollection<SequenceVariable> Variables { get; } = [];
    public ObservableCollection<SimulationValueRow> SimulationValues { get; } = [];
    public ObservableCollection<SequenceLogEntry> RunLogs { get; } = [];
    public IReadOnlyList<string> VariableTypes => SequenceValueOperations.DataTypes;
    public IReadOnlyList<string> VariableScopes { get; } = ["local", "global", "station", "parameter"];
    public IReadOnlyList<string> FailureActions { get; } = ["STOP", "CONTINUE", "GOTO_CLEANUP"];
    public string StepOutcomeLabel => SelectedRow?.HasJudgment == true ? "失败后的动作" : "完成后的动作";
    public string StepOutcomeAction
    {
        get => SelectedRow?.HasJudgment == true ? StepOnFailure : StepOnComplete;
        set
        {
            if (SelectedRow?.HasJudgment == true) StepOnFailure = value;
            else StepOnComplete = value;
            OnPropertyChanged();
        }
    }
    public IReadOnlyList<StepPaletteGroup> StepGroups { get; } =
    [
        new(UiText.FlowControl, [new("IF", "flow"), new("FOR", "flow"), new("WHILE", "flow"),
            new("BREAK", "flow"), new("CONTINUE", "flow")]),
        new(UiText.TestActions, [new("INITIALIZE", "action"), new("SET VALUE", "action"),
            new("GET VALUE", "action"), new("WAIT", "timing"), new("ASSERT", "comparison")]),
        new(UiText.Utility, [new("SEQUENCE CALL", "sequence"), new("LOG", "utility"),
            new("STATEMENT", "utility"), new("COMMENT", "utility")])
    ];
    public string? ProjectPath => _workspace?.Root;
    public bool CanRunSequence => CanEditSequence;
    public bool IsExecutionBusy => _runInProgress || ExecutionState is "RUNNING" or "PAUSED";
    public string PauseResumeText => ExecutionState == "PAUSED" ? UiText.Resume : UiText.Pause;
    public string PauseResumeIcon => ExecutionState == "PAUSED" ? "Play" : "Pause";

    public MainViewModel(IAutoTestDisplayService displayService, ISequenceEditingService editingService,
        ISequenceDocumentService? documents = null, ISequenceDialogService? sequenceDialogs = null,
        IEnumerable<ISequenceChannelProvider>? channelProviders = null,
        ISequenceExecutionHost? executionHost = null,
        ISequenceRunPersistence? runPersistence = null,
        IAutomaticWorkspaceService? workspaceService = null,
        ISequenceReportService? reportService = null)
    {
        _channelProviders = channelProviders?.ToArray() ?? [];
        _executionHost = executionHost ?? new OpenTapHostClient();
        _runPersistence = runPersistence ?? new SequenceRunPersistence();
        _workspaceService = workspaceService ?? new AutomaticWorkspaceService();
        _reportService = reportService ?? new SequenceReportService();
        _displayService = displayService;
        _projectSaving.Register(this);
        _editingService = editingService;
        _documents = documents ?? new SequenceDocumentService();
        _sequenceDialogs = sequenceDialogs ?? new CancelSequenceDialogs();
        _executionHost.EventReceived += value => OnUi(() => HandleHostEvent(value));
        // Station variables are loaded with the active automatic workspace so
        // the ViewModel does not own a repository implementation.
        _stationBaseline = JsonSerializer.Serialize(_stationVariables);
    }

    public void OpenProject(string path)
    {
        EnsureWorkspaceCanChange();
        LoadWorkspace(ProjectWorkspace.Open(path));
        Status = $"已打开: {_workspace!.Root}";
    }

    public void CreateProject(string parent, string name)
    {
        EnsureWorkspaceCanChange();
        LoadWorkspace(ProjectWorkspace.Create(parent, name));
        Status = $"已创建: {_workspace!.Root}";
    }

    private void LoadWorkspace(ProjectWorkspace candidate)
    {
        // Read and validate candidate files before replacing the current document.
        var snapshot = _workspaceService.Load(candidate);
        var subprojects = snapshot.Subprojects;
        var simulation = snapshot.SimulationValues;
        AttachSequence(null);
        _documents.Clear();
        _workspace = candidate;
        ProjectName = candidate.Name;
        IsProjectOpen = true;
        OnPropertyChanged(nameof(ProjectPath));
        _selectionSync = true; _changingSubproject = true;
        try
        {
            SequenceSubprojects.Clear();
            _tabSelections.Clear();
            foreach (var project in subprojects)
            {
                SequenceSubprojects.Add(project);
                foreach (var sequence in project.Sequences) _documents.Track(sequence);
            }
            _globalRuntime.Clear();
            _stationVariables.Clear(); _stationVariables.AddRange(snapshot.StationVariables);
            _workspaceBaseline = JsonSerializer.Serialize(SequenceSubprojects);
        }
        finally { _selectionSync = false; _changingSubproject = false; }
        LoadSimulationValues(simulation);
        ActivateSubproject(SequenceSubprojects.FirstOrDefault());
        ExecutionState = "IDLE"; ExecutionSummary = ""; RunLogs.Clear();
        ResetRunInspection(); CurrentCallPath = "尚未运行"; ResultDescription = "尚无运行结果";
        SelectedRunReport = null;
        SimulationScenarioName = ""; ActiveSimulationScenario = "当前模拟数据";
        LoadSimulationScenarios(); LoadRunReports();
    }

    public void SelectSequence(SequenceDefinition? sequence) => _ = SelectSequenceAsync(sequence);

    partial void OnSelectedSequenceChanged(SequenceDefinition? value)
    {
        if (_selectionSync || _loadingEditors) return;
        RevertSequenceSelection();
        _ = SelectSequenceAsync(value);
    }

    [RelayCommand(CanExecute = nameof(CanManageSequences))]
    private async Task NewSequence()
    {
        if (!CanManageSequences) return;
        _documentOperation = true; NotifyDocumentState();
        try
        {
            if (!await ResolveUnsavedSequenceAsync()) return;
            if (_activeSubproject is null) return;
            var number = 1;
            while (Sequences.Any(sequence => sequence.Name == $"子序列 {number}")) number++;
            var sequence = new SequenceDefinition { Id = Guid.NewGuid().ToString("N"), Name = $"子序列 {number}", Role = "sub" };
            _documents.Track(sequence, saved: false);
            _activeSubproject.Sequences.Add(sequence); Sequences.Add(sequence); AttachSequence(sequence);
            Status = UiText.NewSequenceUnsaved;
        }
        finally { _documentOperation = false; NotifyDocumentState(); }
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void SaveSequence() { if (CanEditSequence) TrySaveSequence(); }

    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private void SaveProject()
    {
        if (!CanSaveProject) return;
        var result = _projectSaving.Save();
        Status = result.Success ? UiText.Get("ProjectSaved") : UiText.Format("ProjectSaveFailed", result.Error);
    }

    public ProjectSaveResult SaveProjectChanges()
    {
        if (!CanSaveProject || _workspace is null) return new(false, "当前工程不可保存");
        if (_sequence is not null && !TryCommitPendingSequenceEdits()) return new(false, Status);
        if (HasPendingSimulationEdits && !TryApplySimulation()) return new(false, Status);
        if (!SaveAutomaticWorkspace()) return new(false, Status);
        NotifyDocumentState();
        return new(true);
    }

    public void ToggleGroup(string section)
    {
        // Folding is a view operation; preserve unfinished input and selection.
        var selectedId = SelectedRow?.Step?.Id;
        if (_documentOperation || !IsExecutionBusy && !TryCommitPendingSequenceEdits()) return;
        if (!_collapsed.Add(section)) _collapsed.Remove(section);
        RefreshDocumentEditors(selectedId);
    }

    public void AddStep(string displayKind, SequenceInsertTarget? insertion = null)
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        try
        {
            var target = insertion ?? GetInsertionAfter(SelectedRow);
            SequenceStep? step = null;
            _document!.Edit(sequence => {
                step = _editingService.Insert(sequence, displayKind, target);
                step.Name = UiDisplay.StepType(step.Kind);
            });
            _collapsed.Remove(target.Section);
            RefreshDocumentEditors(step!.Id);
            Status = UiText.Format(nameof(UiText.StepAdded), UiDisplay.StepType(displayKind));
        }
        catch (ArgumentException error) { Status = error.Message; }
    }

    public void MoveStep(string stepId, SequenceInsertTarget target)
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        try
        {
            if (!_document!.Edit(sequence => _editingService.Move(sequence, stepId, target))) return;
            _collapsed.Remove(target.Section);
            RefreshDocumentEditors(stepId);
            Status = "步骤顺序已更新，保存后写入工程";
        }
        catch (ArgumentException error) { Status = error.Message; }
    }

    public SequenceInsertTarget GetInsertionAfter(SequenceRow? row)
    {
        if (row is null) return new SequenceInsertTarget("Main", null, false, _sequence?.Steps.Count ?? 0);
        return row.RowType == "group" ? row.AfterTarget! : row.IntoTarget ?? row.AfterTarget!;
    }

    [RelayCommand(CanExecute = nameof(CanEditSequence))]
    private void DeleteStep()
    {
        if (SelectedRow is { } row) DeleteSteps([row]);
    }

    private void RefreshRows()
    {
        var loading = _loadingEditors; _loadingEditors = true;
        try
        {
            var id = SelectedRow?.Step?.Id;
            Rows.Clear();
            foreach (var row in _displayService.BuildRows(_sequence, _collapsed, _stepStatuses))
            {
                row.CanEdit = CanEditSequence; row.ChangeEnabled = SetStepEnabled; Rows.Add(row);
            }
            SelectedRow = Rows.FirstOrDefault(row => row.Step?.Id == id && row.Step is not null);
        }
        finally { _loadingEditors = loading; }
    }

    public bool CanMoveStep(string id, SequenceInsertTarget target) =>
        CanEditSequence && _editingService.CanMove(_sequence!, id, target);

    public void DeleteSteps(IEnumerable<SequenceRow> rows)
    {
        if (!CanEditSequence) return;
        var ids = SelectedIds(rows);
        if (ids.Length == 0 || !TryCommitPendingSequenceEdits()) return;
        _document!.Edit(sequence => _editingService.Remove(sequence, ids));
        RefreshDocumentEditors(); Status = $"已删除 {ids.Length} 个步骤，保存后写入工程";
    }

    public void CopySteps(IEnumerable<SequenceRow> rows)
    {
        if (_sequence is null || _documentOperation || !IsExecutionBusy && !TryCommitPendingSequenceEdits()) return;
        _editingService.Copy(_sequence, SelectedIds(rows));
        Status = _editingService.CanPaste ? "步骤及其子步骤已复制" : "没有可复制的步骤";
    }

    public void CutSteps(IEnumerable<SequenceRow> rows)
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        var ids = SelectedIds(rows);
        _document!.Edit(sequence => _editingService.Cut(sequence, ids));
        RefreshDocumentEditors(); Status = "步骤已剪切";
    }

    public void PasteSteps()
    {
        if (!CanEditSequence || !TryCommitPendingSequenceEdits()) return;
        try
        {
            var target = GetInsertionAfter(SelectedRow);
            IReadOnlyList<SequenceStep> pasted = [];
            _document!.Edit(sequence => pasted = _editingService.Paste(sequence, target));
            _collapsed.Remove(target.Section); RefreshDocumentEditors(pasted.FirstOrDefault()?.Id);
            Status = $"已粘贴 {pasted.Count} 个步骤";
        }
        catch (ArgumentException error) { Status = error.Message; }
    }

    public void MoveSelectedStep(int offset)
    {
        if (!CanEditSequence || SelectedRow?.RowType != "step" || SelectedRow.Step is null) return;
        var row = SelectedRow;
        MoveStep(row.Step.Id, row.BeforeTarget! with { Index = Math.Max(0, row.ListIndex + (offset < 0 ? -1 : 2)) });
    }

    private static string[] SelectedIds(IEnumerable<SequenceRow> rows) =>
        rows.Where(row => row.RowType == "step" && row.Step is not null)
            .Select(row => row.Step!.Id).Distinct().ToArray();
}
