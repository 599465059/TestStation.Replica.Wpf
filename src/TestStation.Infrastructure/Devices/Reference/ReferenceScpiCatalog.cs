using System.Reflection;
using System.Text.Json;
using TestStation.Domain.Devices;

namespace TestStation.Infrastructure.Devices.Reference;

internal sealed class ReferenceScpiCatalog
{
    public IReadOnlyList<ScpiFamilyData> Families { get; }
    public ReferenceScpiCatalog()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "TestStation.Infrastructure.Devices.Reference.Data.reference-scpi-profiles.json")
            ?? throw new InvalidDataException("参考 SCPI 能力清单未嵌入程序");
        Families = JsonSerializer.Deserialize<List<ScpiFamilyData>>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (Families.Count == 0) throw new InvalidDataException("参考 SCPI 能力清单为空");
        foreach (var family in Families) Validate(family);
    }
    public InstrumentFamily ToDefinition(ScpiFamilyData family) => new(family.FamilyId, family.Name, family.Category,
        family.Models.Select(model => new InstrumentModel(model.Id, model.Id, family.Name, "scpi", ["tcp", "serial"],
            Channels(model.Definition), family.SourceModule)).ToArray());
    private static IReadOnlyList<InstrumentChannel> Channels(ScpiCommandSet set)
    {
        var channels = new List<InstrumentChannel>();
        var count = Math.Max(1, set.ChannelCount);
        for (var channel = 1; channel <= count; channel++)
        {
            var prefix = count == 1 ? "" : $"CH{channel}.";
            channels.AddRange(set.Parameters.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p =>
                new InstrumentChannel(prefix + p.Name, p.DisplayName, p.Type, p.Unit ?? "", !string.IsNullOrWhiteSpace(p.QueryCommand),
                    !p.IsReadOnly && !string.IsNullOrWhiteSpace(p.WriteCommand), p.IsMeasured, p.MinValue, p.MaxValue)));
            channels.AddRange(set.Commands.Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Name)).Select(c =>
                new InstrumentChannel(prefix + "action." + c.Name, c.DisplayName, c.CommandType == "Query" ? "string" : "boolean", "", c.CommandType == "Query", c.CommandType == "Command", false, IsAction: c.CommandType == "Command")));
        }
        return channels;
    }
    private static void Validate(ScpiFamilyData family)
    {
        if (string.IsNullOrWhiteSpace(family.FamilyId) || family.Models.Count == 0) throw new InvalidDataException("参考 SCPI 家族定义不完整");
        if (family.Models.Any(model => string.IsNullOrWhiteSpace(model.Id) || model.Definition is null)) throw new InvalidDataException($"家族 {family.FamilyId} 的型号定义不完整");
        foreach (var model in family.Models)
        {
            var names = model.Definition.Parameters.Select(p => p.Name).Concat(model.Definition.Commands.Select(c => "action." + c.Name));
            if (names.Any(string.IsNullOrWhiteSpace) || names.Count() != names.Distinct(StringComparer.OrdinalIgnoreCase).Count()) throw new InvalidDataException($"型号 {model.Id} 含重复通道");
        }
    }
}
