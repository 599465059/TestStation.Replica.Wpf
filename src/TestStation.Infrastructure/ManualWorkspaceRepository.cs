using System.Text.Json;
using TestStation.Application;

namespace TestStation.Infrastructure;

public sealed class ManualWorkspaceRepository : IManualWorkspaceRepository
{
    private static string FilePath(string root) => Path.Combine(root, "layouts", "manual", "workspaces.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public IReadOnlyList<ManualWorkspaceEntry> Load(string projectRoot)
    {
        var path = FilePath(projectRoot);
        if (!File.Exists(path)) return [new("main", "手动测试")];
        var entries = JsonSerializer.Deserialize<List<ManualWorkspaceEntry>>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("手动测试工区目录为空");
        Validate(entries);
        return entries;
    }

    public void Save(string projectRoot, IReadOnlyCollection<ManualWorkspaceEntry> entries)
    {
        Validate(entries);
        var path = FilePath(projectRoot); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(IReadOnlyCollection<ManualWorkspaceEntry> entries)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            ManualWorkspaceEntry.ValidateId(item.Id);
            if (!ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name)) throw new InvalidDataException("手动测试工区名称或标识无效");
        }
    }
}
