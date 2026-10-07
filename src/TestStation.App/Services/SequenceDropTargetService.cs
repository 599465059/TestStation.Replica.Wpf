using TestStation.App.ViewModels;
using TestStation.Application;

namespace TestStation.App.Services;

public sealed record SequenceDropTarget(SequenceInsertTarget Target, bool BelowRow, int Depth);
public sealed record SequenceInsertionAnchor(int RowIndex, bool BelowRow);

public interface ISequenceDropTargetService
{
    SequenceDropTarget Resolve(SequenceRow row, double verticalFraction, bool intoContainer);
    SequenceInsertionAnchor CanonicalAnchor(IReadOnlyList<SequenceRow> rows, SequenceInsertTarget target);
}

// Pure target calculation; viewport hit testing remains in the UI interaction adapter.
public sealed class SequenceDropTargetService : ISequenceDropTargetService
{
    public SequenceInsertionAnchor CanonicalAnchor(IReadOnlyList<SequenceRow> rows, SequenceInsertTarget target)
    {
        var candidates = new List<(int Priority, SequenceInsertionAnchor Anchor)>();
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.BeforeTarget == target)
                candidates.Add((row.RowType == "step" ? 0 : row.RowType is "branch" or "end" ? 2 : row.RowType == "footer" ? 3 : 6, new(index, false)));
            if (row.AfterTarget == target)
                candidates.Add((row.RowType == "branch" ? 1 : row.RowType == "group" ? 5 : 4, new(index, true)));
        }
        return candidates.OrderBy(candidate => candidate.Priority).ThenBy(candidate => candidate.Anchor.RowIndex)
            .FirstOrDefault().Anchor ?? throw new ArgumentException("插入位置不属于当前显示序列");
    }

    public SequenceDropTarget Resolve(SequenceRow row, double verticalFraction, bool intoContainer)
    {
        if (intoContainer && row.IntoTarget is { } inner)
            return new(inner, true, row.Depth + 1);
        var below = verticalFraction >= 0.5;
        var target = (below ? row.AfterTarget : row.BeforeTarget)
            ?? throw new ArgumentException("此行不是有效插入位置");
        var depth = row.RowType == "branch" || row.RowType == "end" && !below
            || row.IntoTarget is not null && below ? row.Depth + 1 : row.Depth;
        return new(target, below, depth);
    }
}
