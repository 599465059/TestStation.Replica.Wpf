using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public interface ISequenceDocumentService
{
    SequenceDocumentSession Track(SequenceDefinition sequence, bool saved = true);
    SequenceDefinition Duplicate(SequenceDefinition sequence, IEnumerable<string> usedIds);
    void Forget(SequenceDefinition sequence);
    void Clear();
}

/// <summary>Document snapshots and history are independent of the UI and execution engine.</summary>
public sealed class SequenceDocumentService : ISequenceDocumentService
{
    private readonly Dictionary<SequenceDefinition, SequenceDocumentSession> _sessions = [];
    public SequenceDocumentSession Track(SequenceDefinition sequence, bool saved = true)
    {
        if (!_sessions.TryGetValue(sequence, out var session))
            _sessions[sequence] = session = new SequenceDocumentSession(sequence, saved);
        return session;
    }
    public void Forget(SequenceDefinition sequence) => _sessions.Remove(sequence);
    public void Clear() => _sessions.Clear();
    public SequenceDefinition Duplicate(SequenceDefinition sequence, IEnumerable<string> usedIds)
    {
        var copy = SequenceDocumentSession.Clone(sequence);
        var ids = usedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        do { copy.Id = "sequence_" + Guid.NewGuid().ToString("N"); } while (ids.Contains(copy.Id));
        foreach (var step in SequenceEditor.Walk(copy)) step.Id = "step_" + Guid.NewGuid().ToString("N");
        return copy;
    }
}

public sealed class SequenceDocumentSession
{
    private const int HistoryLimit = 100;
    private readonly List<string> _undo = [];
    private readonly List<string> _redo = [];
    private string? _saved;
    public SequenceDefinition Model { get; }
    public bool IsSaved => _saved is not null;
    public bool IsDirty => _saved is null || Snapshot() != _saved;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    internal SequenceDocumentSession(SequenceDefinition model, bool saved)
    {
        Model = model;
        if (saved) _saved = Snapshot();
    }

    public bool Edit(Action<SequenceDefinition> edit)
    {
        var before = Snapshot();
        try { edit(Model); }
        catch { if (Snapshot() != before) Restore(before); throw; }
        if (Snapshot() == before) return false;
        _undo.Add(before);
        if (_undo.Count > HistoryLimit) _undo.RemoveAt(0);
        _redo.Clear();
        Changed?.Invoke();
        return true;
    }
    public bool Undo() => Traverse(_undo, _redo);
    public bool Redo() => Traverse(_redo, _undo);
    private bool Traverse(List<string> source, List<string> destination)
    {
        if (source.Count == 0) return false;
        destination.Add(Snapshot());
        Restore(source[^1]); source.RemoveAt(source.Count - 1);
        Changed?.Invoke(); return true;
    }
    public void MarkSaved() { _saved = Snapshot(); Changed?.Invoke(); }
    public bool Discard()
    {
        if (_saved is null) return false;
        Restore(_saved); _undo.Clear(); _redo.Clear(); Changed?.Invoke(); return true;
    }
    private string Snapshot() => JsonSerializer.Serialize(Model);
    private void Restore(string snapshot)
    {
        var restored = JsonSerializer.Deserialize<SequenceDefinition>(snapshot)!;
        Model.Id = restored.Id; Model.Name = restored.Name; Model.Version = restored.Version;
        Model.Description = restored.Description;
        Model.SetupSteps = restored.SetupSteps; Model.Steps = restored.Steps;
        Model.CleanupSteps = restored.CleanupSteps; Model.Variables = restored.Variables;
    }
    public static SequenceDefinition Clone(SequenceDefinition sequence) =>
        JsonSerializer.Deserialize<SequenceDefinition>(JsonSerializer.Serialize(sequence))!;
}
