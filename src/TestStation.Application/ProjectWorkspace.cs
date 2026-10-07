using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public sealed record ProjectWorkspace(string Root, string Name)
{
    private const string ProjectFormat = "test-station-project";
    private static readonly string[] Directories = [
        "devices", "transports", "channels", "layouts/manual", "layouts/sequence",
        "sequences", "protocols/custom", "protocols/dbc", "dbc", "resources", "reports", "logs", "assets"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ProjectWorkspace Create(string parent, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("工程名称无效", nameof(name));
        var root = Path.GetFullPath(Path.Combine(parent, name));
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("工程目录不是空目录");
        Directory.CreateDirectory(root);
        foreach (var directory in Directories)
            Directory.CreateDirectory(Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)));
        var now = DateTimeOffset.UtcNow.ToString("O");
        File.WriteAllText(Path.Combine(root, "project.json"), JsonSerializer.Serialize(new
        {
            format = ProjectFormat, version = 1, name, created_at = now, updated_at = now
        }, JsonOptions));
        return new ProjectWorkspace(root, name);
    }

    public static ProjectWorkspace Open(string path)
    {
        var root = Path.GetFullPath(Path.GetFileName(path).Equals("project.json", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(path) ?? path : path);
        var manifestPath = Path.Combine(root, "project.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("工程目录缺少 project.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var data = manifest.RootElement;
        if (!data.TryGetProperty("format", out var format) || format.GetString() != ProjectFormat)
            throw new InvalidDataException("不是 Test Station 工程目录");
        if (data.TryGetProperty("version", out var version) && version.GetInt32() > 1)
            throw new InvalidDataException("工程格式版本高于当前软件支持的版本");
        return new ProjectWorkspace(root, data.GetProperty("name").GetString() ?? Path.GetFileName(root));
    }

    public IReadOnlyList<SequenceDefinition> LoadSequences()
    {
        var folder = Path.Combine(Root, "sequences");
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.tsseq")
            .Select(path => ReadSequence(path))
            .OfType<SequenceDefinition>().OrderBy(sequence => sequence.Name).ToArray();
    }

    public void SaveSequence(SequenceDefinition sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence.Id) || sequence.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            sequence.Id is "." or "..")
            throw new InvalidDataException("序列 ID 无效");
        var folder = Path.Combine(Root, "sequences");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, sequence.Id + ".tsseq");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            format = "test-station-sequence", version = 1, sequence
        }, JsonOptions));
        File.Move(temporary, target, true);
    }

    public void DeleteSequence(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id is "." or "..")
            throw new InvalidDataException("序列 ID 无效");
        File.Delete(Path.Combine(Root, "sequences", id + ".tsseq"));
    }

    private static SequenceDefinition? ReadSequence(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.TryGetProperty("format", out var format) && format.GetString() != "test-station-sequence")
            throw new InvalidDataException($"不是 Test Station 序列文件: {Path.GetFileName(path)}");
        if (root.TryGetProperty("version", out var version) && version.GetInt32() > 1)
            throw new InvalidDataException($"序列文件版本过高: {Path.GetFileName(path)}");
        var payload = root.TryGetProperty("sequence", out var wrapped) ? wrapped : root;
        return payload.Deserialize<SequenceDefinition>();
    }
}
