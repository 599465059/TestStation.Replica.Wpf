using System.Text.Json;

namespace TestStation.Application;

public sealed class RunResultRepository(string projectRoot)
{
    private const string Format = "test-station-sequence-run";
    private readonly string _root = Path.Combine(projectRoot, "reports");
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public void Save(SequenceRunResult result)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, result.Id + ".tsrun.json");
        var payload = new { format = Format, version = 1, result };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(payload, Options));
        File.Move(temporary, path, true);
    }

    public IReadOnlyList<JsonElement> LoadAll()
    {
        if (!Directory.Exists(_root)) return [];
        var results = new List<JsonElement>();
        foreach (var path in Directory.EnumerateFiles(_root, "*.tsrun.json").OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("format", out var format) && format.GetString() != Format)
                continue;
            results.Add(document.RootElement.Clone());
        }
        return results;
    }
}
