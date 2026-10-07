using System.Text.Json;
using TestStation.Domain.Protocols;

namespace TestStation.Application.Protocols;

public sealed record ProtocolLibrarySnapshot(IReadOnlyList<ProtocolDefinition> Protocols, IReadOnlyList<string> Errors);
public interface IProtocolRepository
{
    ProtocolLibrarySnapshot Load(string root);
    void Save(string root, string? originalId, ProtocolDefinition protocol);
    void Remove(string root, string id);
    ProtocolDefinition Read(string path);
    void Export(string path, ProtocolDefinition protocol);
}

public interface IProtocolLibraryService
{
    event EventHandler? Changed;
    string? ProjectRoot { get; }
    IReadOnlyList<ProtocolDefinition> Protocols { get; }
    IReadOnlyList<string> Errors { get; }
    void OpenProject(string root);
    ProtocolDefinition? Get(string id);
    void Save(string? originalId, ProtocolDefinition protocol);
    void Remove(string id);
    ProtocolDefinition Import(string path);
    void Export(string id, string path);
}

public sealed class ProtocolLibraryService(IProtocolRepository repository) : IProtocolLibraryService
{
    private readonly List<ProtocolDefinition> _protocols = [];
    public event EventHandler? Changed;
    public string? ProjectRoot { get; private set; }
    public IReadOnlyList<ProtocolDefinition> Protocols => _protocols.Select(ProtocolCopy.Clone).ToArray();
    public IReadOnlyList<string> Errors { get; private set; } = [];
    public void OpenProject(string root)
    {
        var snapshot = repository.Load(root);
        ProjectRoot = root;
        _protocols.Clear();
        _protocols.AddRange(snapshot.Protocols);
        Errors = snapshot.Errors;
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public ProtocolDefinition? Get(string id) => _protocols.FirstOrDefault(protocol => protocol.Id == id) is { } protocol ? ProtocolCopy.Clone(protocol) : null;
    public void Save(string? originalId, ProtocolDefinition protocol)
    {
        protocol.Validate();
        if (_protocols.Any(item => item.Id.Equals(protocol.Id, StringComparison.OrdinalIgnoreCase) && item.Id != originalId)) throw new ArgumentException("协议 ID 已存在");
        if (originalId is not null && _protocols.All(item => item.Id != originalId)) throw new ArgumentException("待更新协议不存在");
        repository.Save(Root(), originalId, protocol);
        _protocols.RemoveAll(item => item.Id == originalId);
        _protocols.Add(ProtocolCopy.Clone(protocol));
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Remove(string id)
    {
        repository.Remove(Root(), id);
        _protocols.RemoveAll(protocol => protocol.Id == id);
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public ProtocolDefinition Import(string path)
    {
        var protocol = repository.Read(path);
        var baseId = protocol.Id;
        for (var index = 2; _protocols.Any(item => item.Id.Equals(protocol.Id, StringComparison.OrdinalIgnoreCase)); index++) protocol.Id = $"{baseId}_{index}";
        Save(null, protocol);
        return ProtocolCopy.Clone(protocol);
    }
    public void Export(string id, string path) => repository.Export(path, Get(id) ?? throw new ArgumentException("协议不存在"));
    private string Root() => ProjectRoot ?? throw new InvalidOperationException("请先打开或创建工程");
}

public static class ProtocolCopy
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;
}

public interface IProtocolEditingService
{
    ProtocolDefinition Update(ProtocolDefinition protocol, Action<ProtocolDefinition> change);
    ProtocolDefinition AddFrame(ProtocolDefinition protocol, ProtocolFrame frame);
    ProtocolDefinition CopyFrame(ProtocolDefinition protocol, string id);
    ProtocolDefinition RemoveFrame(ProtocolDefinition protocol, string id);
    ProtocolDefinition UpdateFrame(ProtocolDefinition protocol, string originalId, ProtocolFrame frame);
    ProtocolDefinition AddField(ProtocolDefinition protocol, string frameId, ProtocolField field);
    ProtocolDefinition FillReservedGaps(ProtocolDefinition protocol, string frameId);
    ProtocolDefinition UpdateField(ProtocolDefinition protocol, string frameId, string originalId, ProtocolField field);
    ProtocolDefinition MoveField(ProtocolDefinition protocol, string frameId, string fieldId, int offset);
    ProtocolDefinition RemoveField(ProtocolDefinition protocol, string frameId, string fieldId);
}

public sealed class ProtocolEditingService : IProtocolEditingService
{
    public ProtocolDefinition Update(ProtocolDefinition protocol, Action<ProtocolDefinition> change)
    {
        var draft = ProtocolCopy.Clone(protocol);
        change(draft);
        draft.Validate();
        return draft;
    }
    public ProtocolDefinition AddFrame(ProtocolDefinition protocol, ProtocolFrame frame) => Update(protocol, draft => draft.Frames.Add(ProtocolCopy.Clone(frame)));
    public ProtocolDefinition CopyFrame(ProtocolDefinition protocol, string id) => Update(protocol, draft =>
    {
        var frame = ProtocolCopy.Clone(Frame(draft, id));
        var baseId = frame.Id;
        for (var index = 2; draft.Frames.Any(item => item.Id == frame.Id); index++) frame.Id = $"{baseId}_{index}";
        frame.Name += " 副本";
        draft.Frames.Add(frame);
    });
    public ProtocolDefinition RemoveFrame(ProtocolDefinition protocol, string id) => Update(protocol, draft =>
    {
        if (!draft.Frames.Remove(Frame(draft, id))) throw new ArgumentException("帧不存在");
    });
    public ProtocolDefinition UpdateFrame(ProtocolDefinition protocol, string originalId, ProtocolFrame frame) => Update(protocol, draft =>
    {
        var index = draft.Frames.FindIndex(item => item.Id == originalId);
        if (index < 0) throw new ArgumentException("帧不存在");
        var updated = ProtocolCopy.Clone(frame);
        updated.Fields = draft.Frames[index].Fields;
        draft.Frames[index] = updated;
    });
    public ProtocolDefinition AddField(ProtocolDefinition protocol, string frameId, ProtocolField field) => Update(protocol, draft =>
    {
        var frame = Frame(draft, frameId);
        var item = ProtocolCopy.Clone(field);
        frame.Fields.Add(item);
        // A canvas drop is an explicit layout operation. Keep it free-form by
        // extending an existing fixed length when the user drops past it.
        if (frame.FixedLength > 0 && item.EndOffset > frame.FixedLength) frame.FixedLength = item.EndOffset;
    });
    public ProtocolDefinition FillReservedGaps(ProtocolDefinition protocol, string frameId) => Update(protocol, draft =>
    {
        var frame = Frame(draft, frameId);
        var headers = frame.Fields.Where(item => item.Role == "header").ToArray();
        var tails = frame.Fields.Where(item => item.Role == "tail").ToArray();
        if (headers.Length == 0 || tails.Length == 0) throw new ArgumentException("请先定义帧头和帧尾");
        var start = headers.Max(item => item.EndOffset);
        var end = tails.Min(item => item.ByteOffset);
        if (end < start) throw new ArgumentException("帧尾必须位于帧头之后");
        var occupied = frame.Fields.Select(item => (Start: item.ByteOffset, End: item.EndOffset)).OrderBy(item => item.Start).ToArray();
        var cursor = start;
        var suffix = 1;
        var inserted = false;
        foreach (var range in occupied)
        {
            if (range.End <= start) continue;
            if (range.Start >= end) break;
            var gapEnd = Math.Min(range.Start, end);
            if (gapEnd > cursor)
            {
                while (frame.Fields.Any(item => item.Id == $"reserved_{suffix}")) suffix++;
                frame.Fields.Add(new ProtocolField { Id = $"reserved_{suffix++}", Name = "保留", Role = "skip", DataType = "hex", ByteOffset = cursor, Length = gapEnd - cursor });
                inserted = true;
            }
            cursor = Math.Max(cursor, Math.Min(range.End, end));
        }
        if (end > cursor)
        {
            while (frame.Fields.Any(item => item.Id == $"reserved_{suffix}")) suffix++;
            frame.Fields.Add(new ProtocolField { Id = $"reserved_{suffix}", Name = "保留", Role = "skip", DataType = "hex", ByteOffset = cursor, Length = end - cursor });
            inserted = true;
        }
        if (!inserted) throw new ArgumentException("帧头和帧尾之间没有空白字节");
    });
    public ProtocolDefinition UpdateField(ProtocolDefinition protocol, string frameId, string originalId, ProtocolField field) => Update(protocol, draft =>
    {
        var frame = Frame(draft, frameId);
        var index = frame.Fields.FindIndex(item => item.Id == originalId);
        if (index < 0) throw new ArgumentException("字段不存在");
        frame.Fields[index] = ProtocolCopy.Clone(field);
        if (frame.LengthFieldId == originalId) frame.LengthFieldId = field.Id;
    });
    public ProtocolDefinition MoveField(ProtocolDefinition protocol, string frameId, string fieldId, int offset) => Update(protocol, draft =>
    {
        var frame = Frame(draft, frameId);
        var field = Field(frame, fieldId);
        field.ByteOffset = offset;
        if (frame.FixedLength > 0 && field.EndOffset > frame.FixedLength) frame.FixedLength = field.EndOffset;
    });
    public ProtocolDefinition RemoveField(ProtocolDefinition protocol, string frameId, string fieldId) => Update(protocol, draft =>
    {
        var frame = Frame(draft, frameId);
        frame.Fields.Remove(Field(frame, fieldId));
        if (frame.LengthFieldId == fieldId) frame.LengthFieldId = "";
    });
    private static ProtocolFrame Frame(ProtocolDefinition protocol, string id) => protocol.Frames.FirstOrDefault(frame => frame.Id == id) ?? throw new ArgumentException("帧不存在");
    private static ProtocolField Field(ProtocolFrame frame, string id) => frame.Fields.FirstOrDefault(field => field.Id == id) ?? throw new ArgumentException("字段不存在");
}

public interface IDbcService
{
    IReadOnlyList<string> Files(string root);
    string Import(string root, string path);
    DbcDatabase Load(string path);
}
