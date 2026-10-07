using System.Numerics;
using System.Text;
using System.Text.Json;
using TestStation.Domain.Protocols;

namespace TestStation.Application.Protocols;

public sealed record ProtocolPreviewField(string Name, int Offset, int Length, string Type, string Hex, string State);
public interface IProtocolPreviewService
{
    byte[] Generate(ProtocolFrame frame);
    IReadOnlyList<ProtocolPreviewField> Parse(ProtocolFrame frame, ReadOnlySpan<byte> bytes);
    byte[] ParseHex(string text);
}

// Faithful offline preview: constants are filled; length/checksum metadata is retained,
// but no unspecified length/checksum convention is silently invented.
public sealed class ProtocolPreviewService : IProtocolPreviewService
{
    public byte[] Generate(ProtocolFrame frame)
    {
        frame.Validate();
        var bytes = new byte[frame.CalculatedLength];
        foreach (var field in frame.Fields.Where(field => field.Enabled && field.ConstantValue is { ValueKind: not JsonValueKind.Null }))
        {
            var constant = field.ConstantValue!.Value;
            byte[] value;
            if (constant.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                var text = constant.ValueKind == JsonValueKind.True ? "1" : constant.ValueKind == JsonValueKind.False ? "0" : constant.GetRawText();
                if (!BigInteger.TryParse(text, out var number) || number < 0) throw new ArgumentException($"字段 {field.Name} 的示例常量应为非负整数");
                value = number.ToByteArray(isUnsigned: true, isBigEndian: field.ByteOrder == "big");
                if (value.Length > field.Length) throw new ArgumentException($"字段 {field.Name} 常量超出字节长度");
                if (field.ByteOrder == "big") value = new byte[field.Length - value.Length].Concat(value).ToArray();
            }
            else
            {
                var text = constant.ToString();
                try { value = ParseHex(text); }
                catch (ArgumentException) { value = Encoding.UTF8.GetBytes(text); }
            }
            value.AsSpan(0, Math.Min(value.Length, field.Length)).CopyTo(bytes.AsSpan(field.ByteOffset));
        }
        return bytes;
    }
    public IReadOnlyList<ProtocolPreviewField> Parse(ProtocolFrame frame, ReadOnlySpan<byte> bytes)
    {
        frame.Validate();
        var rows = new List<ProtocolPreviewField>();
        foreach (var field in frame.Fields.OrderBy(field => field.ByteOffset))
        {
            var length = Math.Min(field.Length, Math.Max(0, bytes.Length - field.ByteOffset));
            var raw = length > 0 ? bytes.Slice(field.ByteOffset, length) : [];
            rows.Add(new(field.Name, field.ByteOffset, field.Length, field.DataType, Convert.ToHexString(raw),
                !field.Enabled ? "禁用" : length < field.Length ? "输入不足" : "完整"));
        }
        return rows;
    }
    public byte[] ParseHex(string text)
    {
        var compact = string.Concat(text.Replace(',', ' ').Where(character => !char.IsWhiteSpace(character)));
        try { return Convert.FromHexString(compact); }
        catch (FormatException error) { throw new ArgumentException("输入不是有效的十六进制字节串", error); }
    }
}
