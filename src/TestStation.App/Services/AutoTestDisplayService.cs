using TestStation.App.ViewModels;
using TestStation.Domain;
using TestStation.Application;

namespace TestStation.App.Services;

public interface IAutoTestDisplayService
{
    IReadOnlyList<SequenceRow> BuildRows(
        SequenceDefinition? sequence,
        ISet<string> collapsedSections,
        IReadOnlyDictionary<string, string> statuses);
}

public sealed class AutoTestDisplayService : IAutoTestDisplayService
{
    public IReadOnlyList<SequenceRow> BuildRows(
        SequenceDefinition? sequence,
        ISet<string> collapsedSections,
        IReadOnlyDictionary<string, string> statuses)
    {
        var rows = new List<SequenceRow>();
        if (sequence is null) return rows;

        AppendSection(rows, "Setup", sequence.SetupSteps, "S", collapsedSections, statuses);
        AppendSection(rows, "Main", sequence.Steps, "", collapsedSections, statuses);
        AppendSection(rows, "Cleanup", sequence.CleanupSteps, "C", collapsedSections, statuses);
        return rows;
    }

    private static void AppendSection(
        ICollection<SequenceRow> rows,
        string section,
        IReadOnlyList<SequenceStep> steps,
        string prefix,
        ISet<string> collapsedSections,
        IReadOnlyDictionary<string, string> statuses)
    {
        var collapsed = collapsedSections.Contains(section);
        rows.Add(new SequenceRow
        {
            RowType = "group",
            Section = section,
            ListIndex = steps.Count,
            GroupStepCount = steps.Count,
            IsCollapsed = collapsed,
            BeforeTarget = new(section, null, false, 0),
            AfterTarget = new(section, null, false, steps.Count)
        });
        if (collapsed) return;

        for (var index = 0; index < steps.Count; index++)
            AppendStep(rows, steps[index], section, null, "root", index, 0, $"{prefix}{index + 1}", statuses);
        rows.Add(new SequenceRow
        {
            RowType = "footer", Section = section, ListIndex = steps.Count,
            BeforeTarget = new(section, null, false, steps.Count),
            AfterTarget = new(section, null, false, steps.Count)
        });
    }

    private static void AppendStep(
        ICollection<SequenceRow> rows,
        SequenceStep step,
        string section,
        string? parentId,
        string branch,
        int index,
        int depth,
        string number,
        IReadOnlyDictionary<string, string> statuses)
    {
        rows.Add(new SequenceRow
        {
            RowType = "step",
            Section = section,
            Step = step,
            ParentId = parentId,
            Branch = branch,
            ListIndex = index,
            Depth = depth,
            Number = number,
            Status = statuses.GetValueOrDefault(step.Id, ""),
            BeforeTarget = new(section, parentId, branch == "else", index),
            AfterTarget = step.Kind is "if" or "loop" or "while"
                ? new(section, step.Id, false, 0)
                : new(section, parentId, branch == "else", index + 1),
            IntoTarget = step.Kind is "if" or "loop" or "while" ? new(section, step.Id, false, 0) : null
        });

        if (step.Kind == "if")
            rows.Add(new SequenceRow
            {
                RowType = "branch",
                Section = section,
                ParentId = step.Id,
                ListIndex = 0,
                Depth = depth,
                Branch = "then",
                BeforeTarget = new(section, step.Id, false, 0),
                AfterTarget = new(section, step.Id, false, 0)
            });

        for (var childIndex = 0; childIndex < step.Children.Count; childIndex++)
            AppendStep(rows, step.Children[childIndex], section, step.Id, "children", childIndex,
                depth + 1, $"{number}.{childIndex + 1}", statuses);

        if (step.Kind == "if")
        {
            rows.Add(new SequenceRow
            {
                RowType = "branch", Section = section, ParentId = step.Id,
                ListIndex = 0, Depth = depth, Branch = "else",
                BeforeTarget = new(section, step.Id, false, step.Children.Count),
                AfterTarget = new(section, step.Id, true, 0)
            });
            for (var childIndex = 0; childIndex < step.ElseChildren.Count; childIndex++)
                AppendStep(rows, step.ElseChildren[childIndex], section, step.Id, "else", childIndex,
                    depth + 1, $"{number}.else.{childIndex + 1}", statuses);
        }
        if (step.Kind is "if" or "loop" or "while")
            rows.Add(new SequenceRow
            {
                RowType = "end", Section = section, ParentId = parentId, ListIndex = index + 1,
                Branch = step.Kind, Depth = depth,
                BeforeTarget = new(section, step.Id, step.Kind == "if",
                    step.Kind == "if" ? step.ElseChildren.Count : step.Children.Count),
                AfterTarget = new(section, parentId, branch == "else", index + 1)
            });
    }
}
