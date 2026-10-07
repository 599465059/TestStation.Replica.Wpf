using TestStation.Domain;

namespace TestStation.Application;

public sealed record SequenceInsertTarget(string Section, string? ParentId, bool ElseBranch, int Index);

public static class SequenceEditor
{
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["IF"] = "if",
        ["FOR"] = "loop",
        ["INITIALIZE"] = "initialize",
        ["SET VALUE"] = "set_value",
        ["GET VALUE"] = "read_value",
        ["WAIT"] = "wait",
        ["ASSERT"] = "assert",
        ["SEQUENCE CALL"] = "call_sequence",
        ["COMMENT"] = "comment"
    };

    public static SequenceStep Insert(SequenceDefinition sequence, string displayKind, SequenceInsertTarget target)
    {
        if (!Kinds.TryGetValue(displayKind, out var kind))
            throw new ArgumentException($"未知步骤类型: {displayKind}", nameof(displayKind));
        var destination = ResolveTarget(sequence, target);
        var used = Walk(sequence).Select(step => step.Id).ToHashSet(StringComparer.Ordinal);
        var id = kind;
        for (var number = 2; used.Contains(id); number++) id = $"{kind}_{number}";
        var step = new SequenceStep { Id = id, Kind = kind, Name = displayKind };
        destination.Insert(Math.Clamp(target.Index, 0, destination.Count), step);
        return step;
    }

    public static void InsertExisting(SequenceDefinition sequence, SequenceStep step, SequenceInsertTarget target)
    {
        var destination = ResolveTarget(sequence, target);
        var ids = Walk(sequence).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in new[] { step }.Concat(Walk(step)))
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
                throw new ArgumentException("步骤 ID 为空或重复", nameof(step));
        destination.Insert(Math.Clamp(target.Index, 0, destination.Count), step);
    }

    public static void ValidateTarget(SequenceDefinition sequence, SequenceInsertTarget target) =>
        _ = ResolveTarget(sequence, target);

    public static bool CanMove(SequenceDefinition sequence, string stepId, SequenceInsertTarget target)
    {
        var source = FindList(sequence, stepId);
        if (source is null) return false;
        var step = source.First(item => item.Id == stepId);
        if (target.ParentId == stepId || Walk(step).Any(child => child.Id == target.ParentId)) return false;
        try { ValidateTarget(sequence, target); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool Move(SequenceDefinition sequence, string stepId, SequenceInsertTarget target)
    {
        var source = FindList(sequence, stepId);
        if (source is null) return false;
        var step = source.First(candidate => candidate.Id == stepId);
        if (target.ParentId == stepId || Walk(step).Any(child => child.Id == target.ParentId)) return false;
        var destination = ResolveTarget(sequence, target);
        var sourceIndex = source.IndexOf(step);
        var destinationIndex = Math.Clamp(target.Index, 0, destination.Count);
        if (ReferenceEquals(source, destination) && sourceIndex < destinationIndex) destinationIndex--;
        if (ReferenceEquals(source, destination) && sourceIndex == destinationIndex) return false;
        source.RemoveAt(sourceIndex);
        destination.Insert(destinationIndex, step);
        return true;
    }

    public static bool Remove(SequenceDefinition sequence, string stepId)
    {
        var source = FindList(sequence, stepId);
        return source?.RemoveAll(step => step.Id == stepId) > 0;
    }

    private static List<SequenceStep> ResolveTarget(SequenceDefinition sequence, SequenceInsertTarget target)
    {
        var roots = target.Section switch
        {
            "Setup" => sequence.SetupSteps,
            "Main" => sequence.Steps,
            "Cleanup" => sequence.CleanupSteps,
            _ => throw new ArgumentException("无效序列区域", nameof(target))
        };
        if (target.ParentId is null)
            return roots;
        var parent = Walk(roots).FirstOrDefault(step => step.Id == target.ParentId)
            ?? throw new ArgumentException("目标父步骤不存在", nameof(target));
        if (target.ElseBranch)
        {
            if (parent.Kind != "if") throw new ArgumentException("只有 IF 有 ELSE 分支", nameof(target));
            return parent.ElseChildren;
        }
        if (parent.Kind is not ("if" or "loop"))
            throw new ArgumentException("目标步骤不能包含子步骤", nameof(target));
        return parent.Children;
    }

    private static List<SequenceStep>? FindList(SequenceDefinition sequence, string stepId)
    {
        foreach (var root in new[] { sequence.SetupSteps, sequence.Steps, sequence.CleanupSteps })
        {
            var found = FindList(root, stepId);
            if (found is not null) return found;
        }
        return null;
    }

    private static List<SequenceStep>? FindList(List<SequenceStep> steps, string stepId)
    {
        if (steps.Any(step => step.Id == stepId)) return steps;
        foreach (var step in steps)
        {
            var found = FindList(step.Children, stepId) ?? FindList(step.ElseChildren, stepId);
            if (found is not null) return found;
        }
        return null;
    }

    public static IEnumerable<SequenceStep> Walk(SequenceDefinition sequence) =>
        Walk(sequence.SetupSteps).Concat(Walk(sequence.Steps)).Concat(Walk(sequence.CleanupSteps));

    private static IEnumerable<SequenceStep> Walk(SequenceStep step) =>
        Walk(step.Children).Concat(Walk(step.ElseChildren));

    private static IEnumerable<SequenceStep> Walk(IEnumerable<SequenceStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var child in Walk(step.Children)) yield return child;
            foreach (var child in Walk(step.ElseChildren)) yield return child;
        }
    }
}
