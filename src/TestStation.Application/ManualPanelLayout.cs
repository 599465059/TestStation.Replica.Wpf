namespace TestStation.Application;

/// <summary>Device panel references only; never serializes views, sessions or connection settings.</summary>
public sealed record ManualPanelReference(string FamilyId, string DeviceId);
public sealed class ManualPanelGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Layout { get; set; } = "tabs";
    public string? ActiveDeviceId { get; set; }
    public List<ManualPanelReference> Panels { get; set; } = [];
}
public sealed class ManualPanelLayout
{
    public int Version { get; set; } = 1;
    public string? ActiveGroupId { get; set; }
    public List<ManualPanelGroup> Groups { get; set; } = [];
    public static void Validate(ManualPanelLayout layout)
    {
        if (layout.Version != 1 || layout.Groups.Count > 100) throw new InvalidDataException("手动面板布局版本或分组数量无效");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in layout.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Id) || !ids.Add(group.Id) || string.IsNullOrWhiteSpace(group.Name) || group.Name.Length > 100 ||
                group.Layout is not ("tabs" or "tile" or "columns" or "quad" or "cascade") || group.Panels.Count > 4)
                throw new InvalidDataException("手动面板分组无效，每组最多放置 4 个设备面板");
            if (group.Panels.Any(p => string.IsNullOrWhiteSpace(p.FamilyId) || string.IsNullOrWhiteSpace(p.DeviceId)) ||
                group.Panels.Select(p => (p.FamilyId.ToUpperInvariant(), p.DeviceId.ToUpperInvariant())).Distinct().Count() != group.Panels.Count)
                throw new InvalidDataException("手动面板设备引用无效或重复");
        }
    }
}
public interface IManualPanelLayoutRepository
{
    ManualPanelLayout Load(string projectRoot);
    void Save(string projectRoot, ManualPanelLayout layout);
}
