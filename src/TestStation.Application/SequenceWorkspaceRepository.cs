using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

/// <summary>The automatic-test workspace is one module of the software project.</summary>
public sealed class SequenceWorkspaceRepository(ProjectWorkspace workspace)
{
    private string FilePath => Path.Combine(workspace.Root, "sequences", "workspace.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<SequenceSubproject> Load()
    {
        if (File.Exists(FilePath))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(FilePath));
            var root = document.RootElement;
            if (root.GetProperty("format").GetString() != "test-station-sequence-workspace" || root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("序列工作区格式或版本不受支持");
            var projects = root.GetProperty("subprojects").Deserialize<List<SequenceSubproject>>() ?? [];
            NormalizeRoles(projects);
            Validate(projects);
            return projects;
        }
        // Migrate legacy sequence files in memory; retain their originals on disk.
        var legacy = workspace.LoadSequences().ToList();
        if (legacy.Count == 0) return [CreateSubproject("序列子工程 1")];
        var called = legacy.SelectMany(SequenceEditor.Walk).Where(s => s.Kind == "call_sequence")
            .Select(s => s.Params.GetValueOrDefault("sequence_id").ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sequence in legacy) sequence.Role = "sub";
        var main = legacy.FirstOrDefault(s => !called.Contains(s.Id));
        if (main is not null) main.Role = "main";
        else legacy.Insert(0, NewMain()); // Keep cyclic legacy calls intact for preflight diagnostics.
        return [new SequenceSubproject { Name = "序列子工程 1", Sequences = legacy }];
    }

    public void Save(IEnumerable<SequenceSubproject> subprojects)
    {
        var projects = subprojects.ToList();
        Validate(projects);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        { format = "test-station-sequence-workspace", version = 1, subprojects = projects }, Options));
        File.Move(temporary, FilePath, true);
    }

    private static void Validate(IReadOnlyList<SequenceSubproject> projects)
    {
        if (projects.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != projects.Count)
            throw new InvalidDataException("序列子工程 ID 重复");
        foreach (var project in projects)
        {
            if (project.Sequences.Count(s => s.IsMain) != 1) throw new InvalidDataException($"{project.Name} 必须且只能有一个主序列");
            if (project.Sequences.Any(s => s.Role is not ("main" or "sub"))) throw new InvalidDataException("序列类型无效");
            if (project.Sequences.Select(s => s.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != project.Sequences.Count)
                throw new InvalidDataException($"{project.Name} 的序列 ID 重复");
        }
    }

    private static void NormalizeRoles(IReadOnlyList<SequenceSubproject> projects)
    {
        foreach (var project in projects)
        {
            if (project.Sequences.Count == 0)
            {
                project.Sequences.Add(NewMain());
                continue;
            }

            // Older workspaces could mark every entry as main. Keep the first
            // main in list order as the project entry point and make all others subs.
            var primary = project.Sequences.FirstOrDefault(sequence => sequence.IsMain) ?? project.Sequences[0];
            foreach (var sequence in project.Sequences) sequence.Role = ReferenceEquals(sequence, primary) ? "main" : "sub";
        }
    }

    public static SequenceSubproject CreateSubproject(string name) => new() { Name = name, Sequences = [NewMain()] };
    public static SequenceDefinition NewMain() => new() { Id = Guid.NewGuid().ToString("N"), Name = "主序列", Role = "main" };
}

/// <summary>Station definitions belong to the application user, across software projects.</summary>
public sealed class StationVariableRepository
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TestStation", "station-variables.json");
    public List<SequenceVariable> Load() => File.Exists(FilePath)
        ? JsonSerializer.Deserialize<List<SequenceVariable>>(File.ReadAllText(FilePath)) ?? [] : [];
    public void Save(IEnumerable<SequenceVariable> variables)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(variables, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FilePath + ".tmp", FilePath, true);
    }
}
