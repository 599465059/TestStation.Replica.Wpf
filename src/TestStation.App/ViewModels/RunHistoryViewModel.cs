using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.Application;

namespace TestStation.App.ViewModels;

public sealed partial class RunHistoryViewModel : ObservableObject
{
    private string? _root;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenReportCommand))] private RunReportSummary? _selectedReport;
    [ObservableProperty] private string _details = UiText.Get("RunHistorySelectHint");
    public ObservableCollection<RunReportSummary> Reports { get; } = [];
    public ObservableCollection<StepRunResult> Steps { get; } = [];
    public bool HasReports => Reports.Count > 0;
    public bool HasSelection => SelectedReport is not null;
    public bool HasSteps => Steps.Count > 0;

    partial void OnSelectedReportChanged(RunReportSummary? value)
    {
        Steps.Clear();
        // Status is a stored identifier (completed / failed / stopped); show the localized text.
        Details = value is null ? UiText.Get("RunHistorySelectHint") : UiText.Format("RunHistoryDetails",
            value.SequenceName, UiDisplay.State(value.Status), value.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            value.FinishedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), value.StepCount);
        if (value is not null && _root is not null)
            foreach (var step in new RunResultRepository(_root).LoadResult(value.Id)?.Steps ?? []) Steps.Add(step);
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(HasSteps));
    }

    [RelayCommand]
    private void Refresh() { if (_root is not null) OpenProject(_root); }

    private bool CanOpenReport() => SelectedReport is not null && File.Exists(SelectedReport.HtmlPath);

    [RelayCommand(CanExecute = nameof(CanOpenReport))]
    private void OpenReport()
    {
        if (!CanOpenReport()) return;
        Process.Start(new ProcessStartInfo(SelectedReport!.HtmlPath) { UseShellExecute = true });
    }

    public void OpenProject(string root)
    {
        _root = root;
        Reports.Clear();
        foreach (var report in new RunResultRepository(root).LoadSummaries()) Reports.Add(report);
        OnPropertyChanged(nameof(HasReports));
        SelectedReport = Reports.FirstOrDefault();
    }
}
