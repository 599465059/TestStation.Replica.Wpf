using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.Infrastructure.Protocols;

public sealed class ProtocolRepository : IProtocolRepository
{
    private const string Format = "test-station-protocol";
    public ProtocolLibrarySnapshot Load(string root)
    {
        var protocols = new List<ProtocolDefinition>();
        var errors = new List<string>();
        var folder = Path.Combine(root, "protocols", "custom");
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.tsproto").Order(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var protocol = Read(file);
                    if (protocols.Any(item => item.Id.Equals(protocol.Id, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("协议 ID 重复");
                    protocols.Add(protocol);
                }
                catch (Exception error) when (error is IOException or JsonException or ArgumentException or OverflowException or InvalidOperationException or UnauthorizedAccessException or FormatException)
                { errors.Add($"{Path.GetFileName(file)}: {error.Message}"); }
            }
        return new(protocols, errors);
    }
    public void Save(string root, string? originalId, ProtocolDefinition protocol)
    {
        protocol.Validate();
        var path = FilePath(root, protocol.Id);
        Export(path, protocol);
        if (originalId is not null && !originalId.Equals(protocol.Id, StringComparison.OrdinalIgnoreCase)) File.Delete(FilePath(root, originalId));
    }
    public void Remove(string root, string id) => File.Delete(FilePath(root, id));
    public ProtocolDefinition Read(string path)
    {
        var data = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new ArgumentException("协议文件为空");
        if (data["format"] is { } format && format.GetValue<string>() != Format) throw new ArgumentException("不是 Test Station 协议文件");
        var wrapped = data["protocol"] is JsonObject;
        if (wrapped && data["version"]?.GetValue<int>() > 1) throw new ArgumentException("协议文件版本过高");
        var payload = (wrapped ? data["protocol"] : data)!.DeepClone().AsObject();
        // Recover files from the initial C# migration; canonical Python versions are integers.
        if (payload["version"] is JsonValue version && version.TryGetValue<string>(out var text))
        {
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                || number < 1 || number > int.MaxValue || number != decimal.Truncate(number))
                throw new ArgumentException("协议版本应为正整数");
            payload["version"] = (int)number;
        }
        DefaultName(payload);
        foreach (var frame in payload["frames"]?.AsArray() ?? [])
        {
            if (frame is JsonObject frameObject) DefaultName(frameObject);
            foreach (var field in frame?["fields"]?.AsArray() ?? [])
            {
                if (field is not JsonObject obj) continue;
                DefaultName(obj);
                Alias(obj, "byte_offset", "offset"); Alias(obj, "length", "field_length");
                Alias(obj, "scale", "factor"); Alias(obj, "value_offset", "valueOffset", "offset_value");
            }
        }
        var protocol = payload.Deserialize<ProtocolDefinition>(ProtocolCopy.JsonOptions) ?? throw new ArgumentException("协议内容无效");
        protocol.Validate();
        return protocol;
    }
    public void Export(string path, ProtocolDefinition protocol)
    {
        protocol.Validate();
        var target = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new { format = Format, version = 1, protocol }, ProtocolCopy.JsonOptions));
            File.Move(temp, target, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static void Alias(JsonObject field, string target, params string[] aliases)
    {
        if (field.ContainsKey(target)) return;
        foreach (var alias in aliases) if (field[alias] is { } value) { field[target] = value.DeepClone(); return; }
    }
    private static void DefaultName(JsonObject item)
    {
        if (!item.ContainsKey("name") && item["id"] is { } id) item["name"] = id.DeepClone();
    }
    private static string FilePath(string root, string id)
    {
        ProtocolDefinition.ValidateId(id, "协议");
        return Path.Combine(root, "protocols", "custom", id + ".tsproto");
    }
}
