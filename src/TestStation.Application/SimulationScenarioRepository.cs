using System.Text.Json;

namespace TestStation.Application;

public sealed record SimulationScenario(string Name, IReadOnlyDictionary<string, JsonElement> Values);

public sealed class SimulationScenarioRepository(string projectRoot)
{
    private readonly string _path = Path.Combine(projectRoot, "channels", "simulation-scenarios.json");
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public IReadOnlyList<SimulationScenario> Load()
    {
        if (!File.Exists(_path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;
        if (!root.TryGetProperty("format", out var format) || format.GetString() != "test-station-simulation-scenarios" ||
            !root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("模拟场景文件格式无效");
        var scenarios = JsonSerializer.Deserialize<SimulationScenario[]>(root.GetProperty("scenarios"), Options) ?? [];
        Validate(scenarios);
        return scenarios.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Save(IReadOnlyList<SimulationScenario> scenarios)
    {
        Validate(scenarios);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new { format = "test-station-simulation-scenarios", version = 1, scenarios }, Options));
        File.Move(temporary, _path, true);
    }

    private static void Validate(IReadOnlyList<SimulationScenario> scenarios)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var scenario in scenarios)
        {
            if (string.IsNullOrWhiteSpace(scenario.Name) || !names.Add(scenario.Name)) throw new InvalidDataException("场景名称为空或重复");
            if (scenario.Values is null) throw new InvalidDataException($"{scenario.Name}: 缺少场景数据");
            foreach (var pair in scenario.Values)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) throw new InvalidDataException($"{scenario.Name}: 模拟通道名称不能为空");
                if (pair.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in pair.Value.EnumerateArray())
                        if (value.ValueKind is JsonValueKind.Array or JsonValueKind.Object or JsonValueKind.Null || value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble()))
                            throw new InvalidDataException($"{scenario.Name}: 模拟数组必须是一维有限标量数组");
                }
                else if (pair.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) ||
                    pair.Value.ValueKind == JsonValueKind.Number && !double.IsFinite(pair.Value.GetDouble()))
                    throw new InvalidDataException($"{scenario.Name}: 模拟值必须为标量或一维数组");
            }
        }
    }
}
