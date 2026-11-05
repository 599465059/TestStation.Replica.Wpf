using System.Text.Json;
using TestStation.Application;

namespace TestStation.Infrastructure;

public sealed class ManualPanelLayoutRepository : IManualPanelLayoutRepository
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static string PathFor(string root) => Path.Combine(root, "layouts", "manual", "device-panels.json");
    public ManualPanelLayout Load(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path)) return new();
        var layout = JsonSerializer.Deserialize<ManualPanelLayout>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("手动面板布局为空");
        ManualPanelLayout.Validate(layout);
        return layout;
    }
    public void Save(string projectRoot, ManualPanelLayout layout)
    {
        ManualPanelLayout.Validate(layout);
        var path = PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(layout, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
