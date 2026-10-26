using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;

namespace TestStation.App.ViewModels;

public sealed partial class RunHistoryViewModel : ObservableObject
{
    private string? _root;
    [ObservableProperty] private RunReportSummary? _selectedReport;
    [ObservableProperty] private string _details = "请选择一条运行记录";
    public ObservableCollection<RunReportSummary> Reports { get; } = [];
    public ObservableCollection<StepRunResult> Steps { get; } = [];

    partial void OnSelectedReportChanged(RunReportSummary? value)
    {
        Steps.Clear();
        Details = value is null ? "请选择一条运行记录" :
            $"序列：{value.SequenceName}\n结果：{value.Status}\n开始：{value.StartedAt:yyyy-MM-dd HH:mm:ss}\n结束：{value.FinishedAt:yyyy-MM-dd HH:mm:ss}\n步骤数：{value.StepCount}";
        if (value is not null && _root is not null)
            foreach (var step in new RunResultRepository(_root).LoadResult(value.Id)?.Steps ?? []) Steps.Add(step);
    }

    [RelayCommand]
    private void Refresh() { if (_root is not null) OpenProject(_root); }

    [RelayCommand]
    private void OpenReport()
    {
        if (SelectedReport is null || !File.Exists(SelectedReport.HtmlPath)) return;
        Process.Start(new ProcessStartInfo(SelectedReport.HtmlPath) { UseShellExecute = true });
    }

    public void OpenProject(string root)
    {
        _root = root;
        Reports.Clear();
        foreach (var report in new RunResultRepository(root).LoadSummaries()) Reports.Add(report);
        SelectedReport = Reports.FirstOrDefault();
    }
}
