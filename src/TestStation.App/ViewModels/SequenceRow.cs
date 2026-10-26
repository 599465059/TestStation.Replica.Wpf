using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Domain;
using TestStation.Application;
using TestStation.App.Localization;

namespace TestStation.App.ViewModels;

public partial class SequenceRow : ObservableObject
{
    public required string RowType { get; init; }
    public required string Section { get; init; }
    public string Branch { get; init; } = "root";
    public string? ParentId { get; init; }
    public int ListIndex { get; init; }
    public SequenceStep? Step { get; init; }
    public int Depth { get; init; }
    public string Number { get; init; } = "";
    public SequenceInsertTarget? BeforeTarget { get; init; }
    public SequenceInsertTarget? AfterTarget { get; init; }
    public SequenceInsertTarget? IntoTarget { get; init; }
    public string Category => Step?.Kind switch
    {
        "if" or "loop" or "while" or "break" or "continue" => "flow",
        "assert" => "comparison",
        "wait" => "timing",
        "call_sequence" => "sequence",
        "comment" or "log" or "statement" => "utility",
        _ => "action"
    };
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusColor))] private string _status = "";
    /// <summary>Only executable step rows have a runtime status. Group, branch, footer and end rows are layout rows.</summary>
    public bool IsExecutableStep => RowType == "step" && Step is not null;
    public bool HasJudgment => Step?.Kind == "assert" || Step?.Kind == "read_value" && HasReadComparison(Step);
    public string StatusText => IsExecutableStep ? UiDisplay.StepState(Status, HasJudgment) : "";
    public string StatusColor => IsExecutableStep ? UiDisplay.StepStateColor(Status, HasJudgment) : "Transparent";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _canEdit = true;
    public Func<SequenceStep, bool, bool>? ChangeEnabled { get; set; }
    public string Label => RowType switch
    {
        "group" => UiDisplay.Section(Section),
        "branch" => Branch.Equals("then", StringComparison.OrdinalIgnoreCase) ? "THEN" : "ELSE",
        "footer" => UiText.DropIntoSection,
        "end" => Branch == "if" ? "END IF" : Branch == "while" ? "END WHILE" : "END FOR",
        _ => Step is null ? "" : UiDisplay.StepName(Step)
    };
    public int GroupStepCount { get; init; }
    public bool IsCollapsed { get; init; }
    public string Type => Step is not null ? UiDisplay.StepType(Step.Kind) : RowType == "branch" ? UiText.Branch : "";
    public bool Enabled
    {
        get => Step?.Enabled ?? false;
        set
        {
            if (Step is null || !CanEdit || Step.Enabled == value) return;
            // All production changes pass through document history and run locks.
            if (ChangeEnabled?.Invoke(Step, value) == true) OnPropertyChanged();
        }
    }
    public string Settings => Step is null ? "" : Step.Params.TryGetValue("signals", out var signals) && signals.ValueKind == System.Text.Json.JsonValueKind.Array
        ? $"{signals.GetArrayLength()} 个信号：" + string.Join("、", signals.EnumerateArray().Take(3).Select(row => row.GetProperty("channel_id").GetString()))
        : string.Join("  ", Step.Params.Take(2).Select(pair => $"{UiDisplay.Parameter(pair.Key)}：{UiDisplay.ParameterValue(pair.Key, pair.Value)}"));

    private static bool HasReadComparison(SequenceStep step)
    {
        if (step.Params.TryGetValue("comparator", out var comparator) &&
            !string.Equals(comparator.ToString(), "NONE", StringComparison.OrdinalIgnoreCase)) return true;
        if (!step.Params.TryGetValue("signals", out var signals) || signals.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
        return signals.EnumerateArray().Any(row => row.TryGetProperty("comparator", out var value) &&
            !string.Equals(value.ToString(), "NONE", StringComparison.OrdinalIgnoreCase));
    }
}
