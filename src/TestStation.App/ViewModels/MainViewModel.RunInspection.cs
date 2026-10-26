using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Data;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.App.Localization;
using TestStation.App.Services;

namespace TestStation.App.ViewModels;

public sealed record RuntimeValueRow(string Scope, string Name, string Value);
public sealed record ResultTableRow(string Name, string Path, DataView Rows);

public partial class MainViewModel
{
    public ObservableCollection<RuntimeValueRow> RuntimeValues { get; } = [];
    public ObservableCollection<StepRunResult> InspectedResults { get; } = [];
    public ObservableCollection<ResultTableRow> ResultTables { get; } = [];
    public ObservableCollection<RunReportSummary> RunReports { get; } = [];
    [ObservableProperty] private RunReportSummary? _selectedRunReport;
    [ObservableProperty] private StepRunResult? _selectedStepResult;
    [ObservableProperty] private string _currentCallPath = "尚未运行";
    [ObservableProperty] private string _currentLoop = "";
    [ObservableProperty] private string _resultDescription = "尚无运行结果";
    [ObservableProperty] private string _stepResultDetails = "选择步骤查看详情";

    private void ResetRunInspection()
    {
        SelectedRunReport = null;
        InspectedResults.Clear(); RuntimeValues.Clear(); ResultTables.Clear();
        SelectedStepResult = null; CurrentCallPath = "等待执行"; CurrentLoop = "";
        ResultDescription = "当前运行结果";
    }

    private void ShowRunResult(SequenceRunResult result)
    {
        InspectedResults.Clear();
        foreach (var step in result.Steps) InspectedResults.Add(step);
        ResultDescription = $"{result.SequenceName} · {UiDisplay.State(result.Status == "completed" ? "PASSED" : result.Status)} · {result.Steps.Count} 条步骤结果" +
            (result.Error.Length > 0 ? $"\n{result.Error}" : "");
        CurrentCallPath = "执行结束"; CurrentLoop = "";
        RuntimeValues.Clear();
        foreach (var pair in result.Values) RuntimeValues.Add(new RuntimeValueRow("最终值", pair.Key, SequenceValueOperations.Display(pair.Value)));
        foreach (var pair in result.FinalSimulationValues ?? result.SimulationValues)
            RuntimeValues.Add(new RuntimeValueRow(result.FinalSimulationValues is null ? "初始模拟通道" : "最终模拟通道", pair.Key,
                SequenceValueOperations.Display(pair.Value)));
        SelectedStepResult = InspectedResults.FirstOrDefault(s => s.Status == "failed") ?? InspectedResults.FirstOrDefault();
    }

    partial void OnSelectedStepResultChanged(StepRunResult? value) => StepResultDetails = value is null ? "选择步骤查看详情" :
        $"{value.SequenceId} / {value.Section} / {value.Name}\n状态：{UiDisplay.State(value.Status)}　耗时：{value.DurationMs} ms\n{value.Message}\n调用路径：{value.Path}\n执行标识：{value.StepRunId}";

    private void HandleInspectionEvent(OpenTapHostEvent value)
    {
        if (value.Type == "execution_snapshot" && value.Payload is { } snapshot)
        {
            CurrentCallPath = snapshot.TryGetProperty("call_stack", out var stack)
                ? string.Join(" → ", stack.EnumerateArray().Select(frame => frame.GetProperty("name").GetString())) + $" / {value.Section} / {value.StepId}" : value.Path;
            CurrentLoop = snapshot.TryGetProperty("loops", out var loops) ? string.Join("；", loops.EnumerateArray().Select(loop => loop.GetString())) : "";
            RuntimeValues.Clear();
            foreach (var (key, label) in new[] { ("variables", "当前序列变量"), ("globals", "标签页全局"), ("station", "软件站变量"), ("channels", "已读通道"), ("simulation_values", "模拟通道") })
                if (snapshot.TryGetProperty(key, out var values))
                    foreach (var pair in values.EnumerateObject()) RuntimeValues.Add(new RuntimeValueRow(label, pair.Name, SequenceParameterValue.Display(pair.Value)));
        }
        if (value.Type == "step_finished" && !(value.Payload is { } data && data.TryGetProperty("record_result", out var record) && record.ValueKind == JsonValueKind.False))
        {
            var step = _runSequences.GetValueOrDefault(value.SequenceId) is { } sequence ? SequenceEditor.Walk(sequence).FirstOrDefault(s => s.Id == value.StepId) : null;
            var duration = value.Payload is { } fields && fields.TryGetProperty("duration_ms", out var ms) ? (long)ms.GetDouble() : 0;
            InspectedResults.Add(new StepRunResult(value.StepId, step?.Name ?? value.StepId, step?.Kind ?? "", value.Status,
                value.Message, "", "", duration, value.SequenceId, value.Section, value.StepRunId, value.Path));
        }
        if (value.Type == "result") AddResultTable(value);
    }

    private void AddResultTable(OpenTapHostEvent value)
    {
        if (value.Payload is not { } payload) return;
        var name = payload.TryGetProperty("name", out var title) ? title.GetString() ?? "结果表" : "结果表";
        var table = new DataTable();
        if (payload.TryGetProperty("columns", out var columns))
        {
            var sources = columns.EnumerateArray().ToArray();
            foreach (var column in sources)
            {
                var label = column.GetProperty("name").GetString() ?? "列";
                var unique = label;
                for (var suffix = 2; table.Columns.Contains(unique); suffix++) unique = label + " " + suffix;
                table.Columns.Add(unique, typeof(string));
            }
            var count = sources.Select(column => column.GetProperty("data").GetArrayLength()).DefaultIfEmpty(0).Max();
            for (var index = 0; index < count; index++)
            {
                var row = table.NewRow();
                for (var column = 0; column < sources.Length; column++)
                {
                    var items = sources[column].GetProperty("data");
                    row[column] = index < items.GetArrayLength() ? SequenceParameterValue.Display(items[index]) : "";
                }
                table.Rows.Add(row);
            }
        }
        ResultTables.Add(new ResultTableRow(name, value.Path, table.DefaultView));
    }

    private void LoadRunReports()
    {
        RunReports.Clear();
        if (_workspace is null) return;
        try
        {
            foreach (var report in _reportService.LoadSummaries(_workspace.Root).OrderByDescending(r => r.StartedAt)) RunReports.Add(report);
        }
        catch (Exception error) { RunLogs.Add(new SequenceLogEntry(DateTimeOffset.Now, "WARN", $"历史报告读取失败: {error.Message}")); }
    }

    partial void OnSelectedRunReportChanged(RunReportSummary? value)
    {
        if (value is null || _workspace is null || IsExecutionBusy) return;
        try
        {
            var result = _reportService.LoadResult(_workspace.Root, value.Id);
            if (result is null) return;
            ShowRunResult(result); ResultTables.Clear();
            var path = _reportService.EventsPath(_workspace.Root, value.Id);
            if (File.Exists(path))
                foreach (var item in JsonSerializer.Deserialize<OpenTapHostEvent[]>(File.ReadAllText(path)) ?? [])
                    if (item.Type == "result") AddResultTable(item);
        }
        catch (Exception error) { Status = $"报告读取失败: {error.Message}"; }
    }

    [RelayCommand]
    private void RefreshRunReports() => LoadRunReports();

    [RelayCommand]
    private void OpenSelectedRunReport()
    {
        if (SelectedRunReport is null || IsExecutionBusy) return;
        try
        {
            if (!File.Exists(SelectedRunReport.HtmlPath)) throw new FileNotFoundException("HTML 报告不存在");
            Process.Start(new ProcessStartInfo(SelectedRunReport.HtmlPath) { UseShellExecute = true });
        }
        catch (Exception error) { Status = $"打开报告失败: {error.Message}"; }
    }
}
