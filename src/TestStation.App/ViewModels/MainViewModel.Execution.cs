using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.App.Localization;
using TestStation.Application;
using TestStation.Domain;
using TestStation.App.Services;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, OpenTapHostEvent> _activeHostSteps = new(StringComparer.Ordinal);
    private readonly Dictionary<(string SequenceId, string StepId), string> _runStepStatuses = [];
    private string? _runningSubprojectId;
    private string? _runningEntrySequenceId;
    [ObservableProperty] private SequenceRow? _currentExecutionRow;
    [RelayCommand]
    private void ValidateSequence()
    {
        if (!TryCommitPendingSequenceEdits()) return;
        var report = SequenceLibraryPreflight.Validate(_sequence, Sequences);
        RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, report.IsValid ? "INFO" : "ERROR",
            report.IsValid ? "预检通过，可以运行" : string.Join("；", report.Errors.Select(issue => UiDisplay.LogMessage(issue.Message)))));
        Status = report.IsValid ? "预检通过" : $"预检失败: {report.Errors.Count} 个问题";
    }

    [RelayCommand(CanExecute = nameof(CanRunSequence))]
    private async Task RunSequence()
    {
        if (!CanRunSequence || !TryCommitPendingSequenceEdits()) return;
        if (HasPendingSimulationEdits && !TryApplySimulation()) return;
        // Saved sequence rows must also respect disabled plugins, including
        // channels used by nested sequence calls. Never fall back to a saved
        // simulation value for a disabled plugin.
        RefreshSignalChannels();
        var installedIds = _channelProviders.OfType<SimulatedPluginChannelProvider>()
            .SelectMany(p => p.InstalledChannels).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledIds = _channelProviders.SelectMany(p => p.GetChannels()).Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? UnavailableChannel(SequenceDefinition sequence)
        {
            if (!visited.Add(sequence.Id)) return null;
            foreach (var step in SequenceEditor.Walk(sequence).Where(s => s.Enabled))
            {
                var channels = step.Params.TryGetValue("signals", out var table)
                    ? SequenceSignalOperation.Read(table).Select(r => r.ChannelId)
                    : step.Params.TryGetValue("channel_id", out var id) ? new[] { id.ToString() } : [];
                foreach (var channel in channels)
                {
                    usedChannels.Add(channel);
                    if ((installedIds.Contains(channel) || _channelRuntime is TestStation.Application.Drivers.IExternalChannelRuntime owned && owned.IsExternalChannel(channel)) && !enabledIds.Contains(channel)) return channel;
                }
                if (step.Kind == "call_sequence" && step.Params.TryGetValue("sequence_id", out var target) &&
                    Sequences.FirstOrDefault(s => s.Id == target.ToString()) is { } child && UnavailableChannel(child) is { } missing) return missing;
            }
            return null;
        }
        if (_sequence is not null && UnavailableChannel(_sequence) is { } unavailable)
        {
            Status = UiText.Format("DeviceChannelUnavailable", unavailable);
            return;
        }
        var report = SequenceLibraryPreflight.Validate(_sequence, Sequences);
        if (!report.IsValid)
        {
            Status = $"预检失败: {UiDisplay.LogMessage(report.Errors[0].Message)}";
            RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status));
            return;
        }
        IReadOnlyDictionary<string, JsonElement> suppliedParameters = new Dictionary<string, JsonElement>();
        var inputRows = _sequence!.Variables.Where(v => v.Scope == "parameter").Select(v => new ParameterInputRow(v)).ToArray();
        if (inputRows.Length > 0)
        {
            _documentOperation = true; NotifyDocumentState();
            try
            {
                var supplied = await _sequenceDialogs.RequestRunParametersAsync(_sequence.Name, inputRows);
                if (supplied is null) { Status = "已取消运行"; return; }
                suppliedParameters = supplied;
            }
            catch (Exception error) { Status = $"运行参数输入失败: {error.Message}"; return; }
            finally { _documentOperation = false; NotifyDocumentState(); }
        }
        _runInProgress = true; NotifyDocumentState();
        try
        {
            _stepStatuses.Clear();
            _runStepStatuses.Clear();
            CurrentExecutionRow = null;
            _activeHostSteps.Clear();
            RunLogs.Clear();
            ResetRunInspection();
            ExecutionState = "RUNNING";
            ExecutionSummary = "运行中";
            Status = "正在启动 OpenTAP 执行器";
            RefreshRows();
            var simulation = SimulationValues.ToDictionary(item => item.Channel, item => JsonElementValue(item.Value), StringComparer.OrdinalIgnoreCase);
            // Family plugins supply simulator defaults at runtime. Explicit
            // project values take precedence and are never overwritten.
            foreach (var provider in _channelProviders)
                foreach (var pair in provider.GetDefaultValues())
                    simulation.TryAdd(pair.Key, JsonElementValue(pair.Value));
            if (_channelRuntime is TestStation.Application.Drivers.IExternalChannelRuntime externalRuntime)
                foreach (var channel in externalRuntime.ExternalChannelIds) simulation.Remove(channel);
            using var automationLease = await _channelRuntime.AcquireAutomationAsync("automatic",
                usedChannels);
            _runSequences = Sequences.ToDictionary(item => item.Id,
                item => JsonSerializer.Deserialize<SequenceDefinition>(JsonSerializer.Serialize(item))!, StringComparer.Ordinal);
            var candidate = _runSequences[_sequence!.Id];
            _runningEntrySequenceId = candidate.Id;
            var formalRun = candidate.IsMain;
            _runningSubprojectId = _activeSubproject!.Id;
            var parameters = candidate.Variables.Where(item => item.Scope == "parameter" && item.Default.HasValue)
                .ToDictionary(item => item.Name, item => JsonElementValue(item.Default!.Value), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in suppliedParameters) parameters[pair.Key] = JsonElementValue(pair.Value);
            var run = _executionHost.RunAsync(candidate, _runSequences.Values.ToArray(), parameters, simulation,
                _activeSubproject.GlobalVariables, _stationVariables,
                _globalRuntime.GetValueOrDefault(_activeSubproject.Id), _stationRuntime);
            _runTask = run;
            var outcome = await run;
            var result = outcome.Result;
            ShowRunResult(result);
            ExecutionState = result.Status == "completed" ? "PASSED" : result.Status.ToUpperInvariant();
            foreach (var row in Rows.Where(row => row.Step is not null && row.Status == "RUNNING"))
            {
                row.Status = ExecutionState == "STOPPED" ? "STOPPED" : "FAILED";
                _stepStatuses[row.Step!.Id] = row.Status;
            }
            ExecutionSummary = UiText.Format(nameof(UiText.ExecutionSummary), UiDisplay.State(ExecutionState), result.Steps.Count);
            Status = UiText.Format(nameof(UiText.ExecutionFinished), UiDisplay.State(ExecutionState));
            if (result.Error.Length > 0) RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now,
                result.Status == "stopped" ? "WARN" : "ERROR", result.Error));
            if (!formalRun) { Status += "（子序列调试，不生成报表）"; ResultDescription = "子序列调试 · " + ResultDescription; }
            if (_workspace is not null && formalRun)
            {
                try
                {
                    var root = _workspace.Root;
                    // Retain result tables and call/iteration identities alongside the existing report format.
                    await _runPersistence.SaveAsync(root, result, outcome.Events);
                    LoadRunReports();
                }
                catch (Exception error)
                {
                    Status = $"运行结果保存失败: {error.Message}";
                    RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status));
                }
            }
        }
        catch (Exception error)
        {
            ExecutionState = "FAILED";
            ExecutionSummary = "运行失败";
            Status = $"序列运行失败: {error.Message}";
            RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status));
        }
        finally
        {
            _activeHostSteps.Clear();
            // Return to the original run/debug entry even if execution was stopped in a child.
            var entry = Sequences.FirstOrDefault(s => s.Id == _runningEntrySequenceId);
            if (entry is not null && !ReferenceEquals(entry, _sequence)) AttachSequence(entry);
            _runInProgress = false; _runTask = null;
            _runningSubprojectId = null;
            _runningEntrySequenceId = null;
            foreach (var row in Rows) row.IsCurrent = false;
            CurrentExecutionRow = null;
            NotifyDocumentState();
        }
    }

    [RelayCommand(CanExecute = nameof(IsExecutionBusy))]
    private async Task PauseResume()
    {
        try
        {
            if (ExecutionState == "PAUSED") await _executionHost.ResumeAsync();
            else if (ExecutionState == "RUNNING")
            {
                await _executionHost.PauseAsync();
                Status = "已请求暂停，等待执行器到达暂停点";
            }
        }
        catch (Exception error) { Status = error.Message; RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status)); }
    }

    [RelayCommand(CanExecute = nameof(IsExecutionBusy))]
    private async Task StopSequence() => await StopAndWaitAsync();

    private void HandleHostEvent(OpenTapHostEvent value)
    {
        if (value.Type is "shared_values" or "execution_snapshot" or "run_finished") AcceptSharedValues(value);
        HandleInspectionEvent(value);
        switch (value.Type)
        {
            case "plan_compiled":
                RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "INFO", value.Message));
                break;
            case "run_started": case "run_resumed": case "run_paused":
                // ResultListener start notification may arrive after a checkpoint's pause acknowledgement.
                if (value.Type != "run_started" || ExecutionState != "PAUSED")
                {
                    ExecutionState = value.Type == "run_paused" ? "PAUSED" : "RUNNING";
                    Status = value.Type == "run_started" ? "OpenTAP 正在执行序列" : value.Message;
                }
                RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "INFO", Status));
                NotifyDocumentState();
                break;
            case "step_started": case "step_finished":
                if (value.StepRunId.Length > 0)
                {
                    if (value.Type == "step_started") _activeHostSteps[value.StepRunId] = value;
                    else _activeHostSteps.Remove(value.StepRunId);
                }
                var status = value.Status.ToUpperInvariant();
                // Step IDs are only unique inside their own sequence.
                _runStepStatuses[(value.SequenceId, value.StepId)] = status;
                if (value.SequenceId == _sequence?.Id)
                {
                    _stepStatuses[value.StepId] = status;
                    foreach (var row in Rows.Where(row => row.Step?.Id == value.StepId)) row.Status = status;
                }
                var current = _activeHostSteps.Values.MaxBy(item => item.Path.Length);
                if (current is not null) FollowExecutionStep(current.SequenceId, current.StepId);
                else
                {
                    foreach (var row in Rows) row.IsCurrent = false;
                    CurrentExecutionRow = null;
                }
                break;
            case "execution_snapshot":
                FollowExecutionStep(value.SequenceId, value.StepId);
                break;
            case "log":
                var level = value.Payload is { } payload && payload.TryGetProperty("level", out var levelElement) ? levelElement.GetString() ?? "INFO" : "INFO";
                var source = value.SequenceId.Length > 0 ? $"[{value.SequenceId}/{value.Section}] " : "";
                RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, level, source + value.Message));
                break;
            case "result":
                var name = value.Payload is { } table && table.TryGetProperty("name", out var title) ? title.GetString() : "";
                RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "INFO", $"已接收结果表: {name} ({value.Path})"));
                break;
            case "error":
                RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, value.Status == "stopped" ? "WARN" : "ERROR", value.Message));
                break;
        }
    }

    private void FollowExecutionStep(string sequenceId, string stepId)
    {
        if (!_runInProgress || _activeSubproject?.Id != _runningSubprojectId) return;
        var sequence = Sequences.FirstOrDefault(s => s.Id == sequenceId);
        if (sequence is null) return;
        // Runtime navigation bypasses manual selection locks; it never commits editor changes.
        if (!ReferenceEquals(_sequence, sequence)) AttachSequence(sequence);
        if (!SequenceEditor.Walk(sequence).Any(s => s.Id == stepId)) return;
        if (_collapsed.Count > 0)
        {
            _collapsed.Clear();
            RefreshDocumentEditors(stepId);
        }
        var current = Rows.FirstOrDefault(row => row.RowType == "step" && row.Step?.Id == stepId);
        foreach (var row in Rows) row.IsCurrent = ReferenceEquals(row, current);
        var loading = _loadingEditors; _loadingEditors = true;
        try { SelectedRow = current; }
        finally { _loadingEditors = loading; }
        CurrentExecutionRow = current;
    }

    private void AcceptSharedValues(OpenTapHostEvent value)
    {
        if (value.Payload is not { } payload || _runningSubprojectId is null) return;
        if (payload.TryGetProperty("globals", out var globals))
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in globals.EnumerateObject()) values[pair.Name] = pair.Value.Clone();
            _globalRuntime[_runningSubprojectId] = values;
        }
        if (payload.TryGetProperty("station", out var station))
        {
            _stationRuntime.Clear();
            foreach (var pair in station.EnumerateObject()) _stationRuntime[pair.Name] = pair.Value.Clone();
        }
    }

    private static object? JsonElementValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => SequenceValueOperations.Unwrap(value)
    };

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

}
