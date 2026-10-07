using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public partial class MainViewModel
{
    [RelayCommand]
    private void ValidateSequence()
    {
        var report = SequencePreflightService.Validate(_sequence);
        RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, report.IsValid ? "INFO" : "ERROR",
            report.IsValid ? "预检通过，可以运行" : string.Join("；", report.Errors.Select(issue => UiDisplay.LogMessage(issue.Message)))));
        Status = report.IsValid ? "预检通过" : $"预检失败: {report.Errors.Count} 个问题";
    }

    [RelayCommand(CanExecute = nameof(CanRunSequence))]
    private async Task RunSequence()
    {
        if (!CanRunSequence || !TryCommitPendingSequenceEdits()) return;
        if (HasPendingSimulationEdits && !TryApplySimulation()) return;
        var report = SequencePreflightService.Validate(_sequence);
        if (!report.IsValid)
        {
            Status = $"预检失败: {UiDisplay.LogMessage(report.Errors[0].Message)}";
            RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status));
            return;
        }
        _runStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runInProgress = true; NotifyDocumentState();
        try
        {
            _stepStatuses.Clear();
            RunLogs.Clear();
            ExecutionSummary = "运行中";
            Status = "正在运行序列";
            RefreshRows();
            var simulation = SimulationValues.ToDictionary(item => item.Channel, item => JsonElementValue(item.Value), StringComparer.OrdinalIgnoreCase);
            _runSequences = Sequences.ToDictionary(item => item.Id,
                item => JsonSerializer.Deserialize<SequenceDefinition>(JsonSerializer.Serialize(item))!, StringComparer.Ordinal);
            var candidate = _runSequences[_sequence!.Id];
            _runTask = Task.Run(() => _executionEngine.RunAsync(candidate, simulationValues: simulation));
            await _runTask;
            Status = UiText.Format(nameof(UiText.ExecutionFinished), UiDisplay.State(ExecutionState));
        }
        catch (Exception error)
        {
            Status = $"序列运行失败: {error.Message}";
            RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "ERROR", Status));
        }
        finally { _runStarted?.TrySetResult(); _runInProgress = false; _runTask = null; NotifyDocumentState(); }
    }

    [RelayCommand(CanExecute = nameof(IsExecutionBusy))]
    private async Task PauseResume()
    {
        if (ExecutionState == "PAUSED") await _executionEngine.ResumeAsync();
        else if (ExecutionState == "RUNNING") await _executionEngine.PauseAsync();
    }

    [RelayCommand(CanExecute = nameof(IsExecutionBusy))]
    private async Task StopSequence() => await StopAndWaitAsync();

    private static object? JsonElementValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.GetRawText()
    };

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

}
