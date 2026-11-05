using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

/// <summary>
/// Run strip on the automatic test page: current step, elapsed time and pass / fail counts,
/// so the operator does not have to search the step table while a run is in progress.
/// Counts come from the Host's step_finished events (every executed step, nested ones included).
/// </summary>
public partial class MainViewModel
{
    private readonly HashSet<string> _passedStepRuns = [];
    private readonly HashSet<string> _failedStepRuns = [];
    private DateTimeOffset _runStartedAt;
    private TimeSpan _pausedFor;
    private DateTimeOffset? _pausedAt;
    private DispatcherTimer? _runClock;

    [ObservableProperty] private string _runElapsed = "00:00";
    [ObservableProperty] private int _runPassedCount;
    [ObservableProperty] private int _runFailedCount;

    public bool ShowRunStrip => ExecutionState is "RUNNING" or "PAUSED";
    public string RunCurrentStep => CurrentExecutionRow is { Step: not null } row
        ? UiText.Format("RunStripCurrentStep", UiDisplay.Section(row.Section), row.Label)
        : UiText.Get("RunStripPreparing");

    partial void OnCurrentExecutionRowChanged(SequenceRow? value) => OnPropertyChanged(nameof(RunCurrentStep));

    partial void OnExecutionStateChanged(string value)
    {
        OnPropertyChanged(nameof(ShowRunStrip));
        if (value == "RUNNING" && _runClock is null) StartRunClock();
        else if (value == "PAUSED") _pausedAt ??= DateTimeOffset.Now;
        if (value == "RUNNING" && _pausedAt is { } pausedAt) { _pausedFor += DateTimeOffset.Now - pausedAt; _pausedAt = null; }
        if (value is not ("RUNNING" or "PAUSED")) StopRunClock();
    }

    private void StartRunClock()
    {
        _passedStepRuns.Clear(); _failedStepRuns.Clear();
        RunPassedCount = 0; RunFailedCount = 0;
        _runStartedAt = DateTimeOffset.Now; _pausedFor = TimeSpan.Zero; _pausedAt = null;
        RunElapsed = "00:00";
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        _runClock = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateRunElapsed(), dispatcher);
        _runClock.Start();
    }

    private void StopRunClock()
    {
        _runClock?.Stop();
        _runClock = null;
    }

    private void UpdateRunElapsed()
    {
        var elapsed = (_pausedAt ?? DateTimeOffset.Now) - _runStartedAt - _pausedFor;
        RunElapsed = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"mm\:ss");
    }

    /// <summary>Called for each step_finished event.</summary>
    private void CountStepResult(string stepRunId, string status)
    {
        if (stepRunId.Length == 0) return;
        if (status.Equals("passed", StringComparison.OrdinalIgnoreCase)) _passedStepRuns.Add(stepRunId);
        else if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)) _failedStepRuns.Add(stepRunId);
        RunPassedCount = _passedStepRuns.Count; RunFailedCount = _failedStepRuns.Count;
    }
}
