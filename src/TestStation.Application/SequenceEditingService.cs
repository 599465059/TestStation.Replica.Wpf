using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public interface ISequenceEditingService
{
    SequenceStep Insert(SequenceDefinition sequence, string displayKind, SequenceInsertTarget target);
    bool CanMove(SequenceDefinition sequence, string stepId, SequenceInsertTarget target);
    bool Move(SequenceDefinition sequence, string stepId, SequenceInsertTarget target);
    void Remove(SequenceDefinition sequence, IEnumerable<string> ids);
    void Copy(SequenceDefinition sequence, IEnumerable<string> ids);
    void Cut(SequenceDefinition sequence, IEnumerable<string> ids);
    IReadOnlyList<SequenceStep> Paste(SequenceDefinition sequence, SequenceInsertTarget target);
    bool CanPaste { get; }
}

// One instance per editor. Clipboard snapshots contain complete subtrees and no UI types.
public sealed class SequenceEditingService : ISequenceEditingService
{
    private readonly List<SequenceStep> _clipboard = [];
    public bool CanPaste => _clipboard.Count > 0;

    public SequenceStep Insert(SequenceDefinition sequence, string displayKind, SequenceInsertTarget target) =>
        SequenceEditor.Insert(sequence, displayKind, target);

    public bool CanMove(SequenceDefinition sequence, string stepId, SequenceInsertTarget target) =>
        SequenceEditor.CanMove(sequence, stepId, target);

    public bool Move(SequenceDefinition sequence, string stepId, SequenceInsertTarget target) =>
        SequenceEditor.Move(sequence, stepId, target);

    public void Remove(SequenceDefinition sequence, IEnumerable<string> ids)
    {
        foreach (var id in ids.Distinct()) SequenceEditor.Remove(sequence, id);
    }

    public void Copy(SequenceDefinition sequence, IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        _clipboard.Clear();
        foreach (var root in new[] { sequence.SetupSteps, sequence.Steps, sequence.CleanupSteps })
            Collect(root, selected);
    }

    private void Collect(IEnumerable<SequenceStep> steps, ISet<string> selected)
    {
        foreach (var step in steps)
        {
            if (selected.Contains(step.Id)) _clipboard.Add(Clone(step));
            else
            {
                Collect(step.Children, selected);
                Collect(step.ElseChildren, selected);
            }
        }
    }

    public void Cut(SequenceDefinition sequence, IEnumerable<string> ids)
    {
        var selected = ids.ToArray();
        Copy(sequence, selected);
        Remove(sequence, selected);
    }

    public IReadOnlyList<SequenceStep> Paste(SequenceDefinition sequence, SequenceInsertTarget target)
    {
        // Resolve/validate the destination before changing the document.
        SequenceEditor.ValidateTarget(sequence, target);
        var pasted = _clipboard.Select(Clone).ToList();
        foreach (var step in pasted) AssignFreshIds(step);
        for (var index = 0; index < pasted.Count; index++)
            SequenceEditor.InsertExisting(sequence, pasted[index], target with { Index = target.Index + index });
        return pasted;
    }

    private static SequenceStep Clone(SequenceStep step) =>
        JsonSerializer.Deserialize<SequenceStep>(JsonSerializer.Serialize(step))!;

    private static void AssignFreshIds(SequenceStep step)
    {
        step.Id = $"step_{Guid.NewGuid():N}";
        foreach (var child in step.Children.Concat(step.ElseChildren)) AssignFreshIds(child);
    }
}
