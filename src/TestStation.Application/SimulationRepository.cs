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

    public IReadOnlyList<SequenceChannelDefinition> LoadDefinitions()
    {
        if (!File.Exists(_path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;
        var definitions = root.TryGetProperty("definitions", out var declared)
            ? declared.Deserialize<List<SequenceChannelDefinition>>() ?? [] : [];
        if (definitions.Any(definition => definition is null || string.IsNullOrWhiteSpace(definition.Id) ||
                !SequenceValueOperations.DataTypes.Contains(definition.DataType)) ||
            definitions.Select(definition => definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count)
            throw new InvalidDataException("模拟通道定义的名称或数据类型无效");
        var known = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Load())
            if (known.Add(pair.Key)) definitions.Add(new(pair.Key, LegacyType(pair.Value)));
        return definitions;
    }

    // Used only when migrating files without definitions. Once saved, changing
    // a scenario value (including an empty array) cannot change the channel type.
    private static string LegacyType(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Array => value.GetArrayLength() == 0 ? "number[]" : LegacyType(value[0]) + "[]",
        JsonValueKind.String => "text",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "number"
    };

    public void Save(IReadOnlyDictionary<string, JsonElement> values, bool preserveDefinitions = false,
        IReadOnlyList<SequenceChannelDefinition>? channelDefinitions = null)
    {
        var validated = values.ToDictionary(pair => pair.Key, pair => Validate(pair.Value));
        if (validated.Keys.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException("模拟通道 ID 不能为空");
        var definitions = (channelDefinitions ?? LoadDefinitions()).Where(definition => preserveDefinitions ||
            validated.Keys.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (definitions.Any(definition => string.IsNullOrWhiteSpace(definition.Id) || !SequenceValueOperations.DataTypes.Contains(definition.DataType)) ||
            definitions.Select(definition => definition.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != definitions.Count)
            throw new InvalidDataException("模拟通道定义的名称或数据类型无效");
        var known = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in validated)
            if (known.Add(pair.Key)) definitions.Add(new(pair.Key, LegacyType(pair.Value)));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = Path.ChangeExtension(_path, ".tmp");
        File.WriteAllText(temporary, JsonSerializer.Serialize(new
        {
            format = "test-station-simulation", version = 1, definitions, values = validated
        }, JsonOptions));
        File.Move(temporary, _path, true);
    }

    private static JsonElement Validate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Array) throw new InvalidDataException("模拟通道只支持一维数组");
                Validate(item);
            }
            return value.Clone();
        }
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("模拟值必须是标量或一维数组");
        if (value.ValueKind == JsonValueKind.Number && !double.IsFinite(value.GetDouble()))
            throw new InvalidDataException("模拟数值必须是有限数字");
        return value.Clone();
    }
}
