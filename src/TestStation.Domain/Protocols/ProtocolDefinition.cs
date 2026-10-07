using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestStation.Domain.Protocols;

public sealed class ProtocolDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Transport { get; set; } = "serial";
    public string Description { get; set; } = "";
    public List<ProtocolFrame> Frames { get; set; } = [];
    public Dictionary<string, JsonElement> Metadata { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public void Validate()
    {
        ValidateId(Id, "协议");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("协议名称不能为空");
        if (Version < 1) throw new ArgumentException("协议版本应为正整数");
        if (Frames is null || Frames.Count == 0 || Frames.Any(frame => frame is null)) throw new ArgumentException("协议至少包含一个有效帧");
        if (Frames.Select(frame => frame.Id).Distinct(StringComparer.Ordinal).Count() != Frames.Count)
            throw new ArgumentException("帧 ID 重复");
        foreach (var frame in Frames) frame.Validate();
    }

    public static void ValidateId(string id, string label)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || id.Any(char.IsControl))
            throw new ArgumentException($"{label} ID 无效");
    }
}

public sealed class ProtocolFrame
{
    public static readonly string[] Kinds = ["read_request", "read_response", "write_request", "write_response", "event"];
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "read_request";
    public List<ProtocolField> Fields { get; set; } = [];
    public int FixedLength { get; set; }
    public string LengthFieldId { get; set; } = "";
    public string ChecksumAlgorithm { get; set; } = "none";
    public int ChecksumStart { get; set; }
    public string Description { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public int CalculatedLength => Math.Max(FixedLength, Fields.Select(item => item.EndOffset).DefaultIfEmpty(0).Max());

    public void Validate()
    {
        ProtocolDefinition.ValidateId(Id, "帧");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("帧名称不能为空");
        if (!Kinds.Contains(Kind)) throw new ArgumentException("帧类型无效");
        if (FixedLength < 0 || FixedLength > 65536) throw new ArgumentException("固定帧长度应为 0–65536 字节");
        if (Fields is null || Fields.Any(item => item is null)) throw new ArgumentException("字段列表无效");
        if (Fields.Select(field => field.Id).Distinct(StringComparer.Ordinal).Count() != Fields.Count) throw new ArgumentException("字段 ID 重复");
        foreach (var field in Fields) field.Validate();
        if (FixedLength > 0 && Fields.Any(field => field.EndOffset > FixedLength)) throw new ArgumentException("字段超出固定帧长度");
        var enabled = Fields.Where(field => field.Enabled).OrderBy(field => field.ByteOffset).ToArray();
        for (var index = 0; index < enabled.Length; index++)
        {
            if (index > 0 && enabled[index].ByteOffset < enabled[index - 1].EndOffset) throw new ArgumentException($"字段 {enabled[index - 1].Name} 与 {enabled[index].Name} 字节重叠");
        }
        if (Fields.Count(field => field.Role == "length") > 1 || Fields.Count(field => field.Role == "checksum") > 1)
            throw new ArgumentException("每帧最多一个长度字段和校验字段");
        if (LengthFieldId.Length > 0 && Fields.All(field => field.Id != LengthFieldId)) throw new ArgumentException("长度字段引用不存在");
        if (ChecksumStart < 0 || ChecksumStart > CalculatedLength) throw new ArgumentException("校验起点超出帧长度");
    }
}

public sealed class ProtocolField
{
    public static readonly string[] Roles = ["header", "id", "length", "data", "checksum", "tail", "skip"];
    public static readonly string[] Types = ["bool", "uint8", "int8", "uint16", "int16", "uint32", "int32", "uint64", "int64", "float32", "float64", "ascii", "hex"];
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "data";
    public string DataType { get; set; } = "uint8";
    public int ByteOffset { get; set; }
    public int Length { get; set; } = 1;
    public string ByteOrder { get; set; } = "big";
    public double Scale { get; set; } = 1;
    public double ValueOffset { get; set; }
    public string ChannelId { get; set; } = "";
    public JsonElement? ConstantValue { get; set; }
    public string Description { get; set; } = "";
    public bool Enabled { get; set; } = true;
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    [JsonIgnore] public int EndOffset => checked(ByteOffset + Length);

    public void Validate()
    {
        ProtocolDefinition.ValidateId(Id, "字段");
        if (string.IsNullOrWhiteSpace(Name)) throw new ArgumentException("字段名称不能为空");
        if (!Roles.Contains(Role) || !Types.Contains(DataType) || ByteOrder is not ("big" or "little")) throw new ArgumentException($"字段 {Name} 类型、角色或字节序无效");
        if (ByteOffset < 0 || Length < 1 || EndOffset > 65536) throw new ArgumentException($"字段 {Name} 字节范围无效");
        if (!double.IsFinite(Scale) || Scale == 0 || !double.IsFinite(ValueOffset)) throw new ArgumentException("比例必须是有限非零数，偏移必须是有限数");
    }
}
