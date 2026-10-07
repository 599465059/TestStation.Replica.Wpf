using System.Text.Json;

namespace TestStation.Application;

public sealed class SimulationRepository(string projectRoot)
{
    private readonly string _path = Path.Combine(projectRoot, "channels", "simulation.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, JsonElement> Load()
    {
        if (!File.Exists(_path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.GetProperty("format").GetString() != "test-station-simulation" ||
            root.GetProperty("version").GetInt32() > 1 || root.GetProperty("values").ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("模拟通道文件格式无效");
        return root.GetProperty("values").EnumerateObject().ToDictionary(pair => pair.Name, pair => Validate(pair.Value));
    }

    public void Save(IReadOnlyDictionary<string, JsonElement> values)
    {
        var validated = values.ToDictionary(pair => pair.Key, pair => Validate(pair.Value));
        if (validated.Keys.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("模拟通道 ID 不能为空");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = Path.ChangeExtension(_path, ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            format = "test-station-simulation", version = 1, values = validated
        }, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static JsonElement Validate(JsonElement value)
    {
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("模拟值必须是文本、数字或布尔值");
        if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble()))
            throw new InvalidDataException("模拟数值必须是有限数字");
        return value.Clone();
    }
}
