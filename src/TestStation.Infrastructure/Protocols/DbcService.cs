using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.Infrastructure.Protocols;

public sealed partial class DbcService : IDbcService
{
    public IReadOnlyList<string> Files(string root)
    {
        var managed = Path.Combine(root, "protocols", "dbc");
        var legacy = Path.Combine(root, "dbc");
        var files = Enumerate(managed);
        return files.Count > 0 ? files : Enumerate(legacy);
    }
    public string Import(string root, string path)
    {
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 .dbc 文件");
        _ = Load(path);
        var content = File.ReadAllBytes(path);
        foreach (var existing in Files(root))
            if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(content)) return existing;
        var folder = Path.Combine(root, "protocols", "dbc");
        Directory.CreateDirectory(folder);
        var name = Path.GetFileNameWithoutExtension(path);
        var target = Path.Combine(folder, name + ".dbc");
        for (var index = 2; File.Exists(target); index++) target = Path.Combine(folder, $"{name}_{index}.dbc");
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, content); File.Move(temp, target, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return target;
    }
    public DbcDatabase Load(string path)
    {
        string text;
        try { text = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            text = File.ReadAllText(path, Encoding.GetEncoding(936));
        }
        var messages = new List<DbcMessage>();
        var warnings = new List<string>();
        var zeroLengthMessages = new HashSet<string>(StringComparer.Ordinal);
        var multiplexedMessages = new HashSet<string>(StringComparer.Ordinal);
        var extensionWarnings = new HashSet<string>(StringComparer.Ordinal);
        List<DbcSignal>? currentSignals = null;
        DbcMessage? current = null;
        var lineNumber = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim().TrimStart('\uFEFF');
            var message = MessagePattern().Match(line);
            if (message.Success)
            {
                var dbcId = uint.Parse(message.Groups["id"].Value, CultureInfo.InvariantCulture);
                var id = (dbcId & 0x80000000) != 0 ? dbcId & 0x1FFFFFFF : dbcId;
                var length = int.Parse(message.Groups["length"].Value, CultureInfo.InvariantCulture);
                if (length is < 0 or > 64 || id > 0x1FFFFFFF) throw new ArgumentException($"DBC 第 {lineNumber} 行报文范围无效");
                if (messages.Any(item => item.Id == id)) throw new ArgumentException($"DBC CAN ID 0x{id:X} 重复");
                currentSignals = [];
                current = new(id, message.Groups["name"].Value, length, message.Groups["tx"].Value, currentSignals);
                messages.Add(current);
                continue;
            }
            if (line.StartsWith("BO_ ", StringComparison.Ordinal)) throw new ArgumentException($"DBC 第 {lineNumber} 行报文定义无效");
            if (line.StartsWith("SG_", StringComparison.Ordinal) && line.Length > 3 && char.IsWhiteSpace(line[3]))
            {
                var match = SignalPattern().Match(line);
                if (!match.Success || currentSignals is null || current is null) throw new ArgumentException($"DBC 第 {lineNumber} 行信号定义无效");
                double Number(string key) => double.Parse(match.Groups[key].Value, CultureInfo.InvariantCulture);
                double? Limit(string key) => match.Groups[key].Value.Trim() == "-" ? null : Number(key);
                var signal = new DbcSignal(match.Groups["name"].Value,
                    int.Parse(match.Groups["start"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture),
                    match.Groups["endian"].Value == "1", match.Groups["sign"].Value == "-", Number("factor"), Number("offset"),
                    Limit("min"), Limit("max"), match.Groups["unit"].Value,
                    match.Groups["rx"].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                if (signal.Factor == 0 || !double.IsFinite(signal.Factor) || !double.IsFinite(signal.Offset)) throw new ArgumentException($"DBC 第 {lineNumber} 行缩放无效");
                if (currentSignals.Any(item => item.Name == signal.Name)) throw new ArgumentException($"DBC 信号 {signal.Name} 重复");
                // A few Vector-generated databases use a zero-length independent
                // message as a container for signals. It is valid to browse such
                // definitions even though the message cannot be encoded as a
                // normal CAN payload. Validate those signal bit ranges against
                // the maximum CAN payload instead of rejecting the whole import.
                try { _ = signal.Extract(new byte[current.Length > 0 ? current.Length : 64]); }
                catch (ArgumentException) when (current.Length == 0)
                {
                    if (zeroLengthMessages.Add(current.Name))
                        warnings.Add($"{current.Name}: 报文长度为 0，仅用于定义浏览，离线编解码不可用");
                }
                if (match.Groups["mux"].Success && multiplexedMessages.Add(current.Name))
                    warnings.Add($"{current.Name}: 包含多路复用信号，仅浏览，本次不实现多路复用选择器");
                currentSignals.Add(signal);
            }
            if (line.StartsWith("SIG_VALTYPE_", StringComparison.Ordinal) || line.StartsWith("SG_MUL_VAL_", StringComparison.Ordinal))
            {
                var extension = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                if (extensionWarnings.Add(extension)) warnings.Add($"包含 {extension} 扩展定义，暂不用于编解码");
            }
        }
        if (messages.Count == 0) throw new ArgumentException("DBC 中没有可识别的 BO_ 报文");
        return new(Path.GetFullPath(path), messages, warnings);
    }
    private static IReadOnlyList<string> Enumerate(string folder) => !Directory.Exists(folder) ? [] :
        Directory.EnumerateFiles(folder).Where(path => path.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    [GeneratedRegex(@"^BO_\s+(?<id>\d+)\s+(?<name>[\w]+)\s*:\s*(?<length>\d+)\s+(?<tx>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex MessagePattern();
    [GeneratedRegex(@"^SG_\s+(?<name>[\w]+)(?:\s+(?<mux>\S+))?\s*:\s*(?<start>\d+)\|(?<size>\d+)@(?<endian>[01])(?<sign>[+-])\s+\((?<factor>[^,]+),(?<offset>[^)]+)\)\s+\[(?<min>[^|]+)\|(?<max>[^\]]+)\]\s+""(?<unit>[^""]*)""\s*(?<rx>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex SignalPattern();
}
