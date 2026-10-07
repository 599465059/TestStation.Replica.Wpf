namespace TestStation.Domain;

// Integer cells are the persisted source of truth, including widget sizes.
public readonly record struct GridBounds(int Column, int Row, int Columns, int Rows);
public static class ManualWidgetKinds
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "numeric", "readout", "input", "serial", "gauge", "status", "command",
        "scope", "slider", "toggle", "mode", "progress", "trigger", "range", "ring", "container"
    };
    public static bool IsSupported(string? kind) => kind is not null && Supported.Contains(kind);
}
// ConfigurationJson is an opaque snapshot of the Industrial.Controls
// InstrumentSettings object.  Keeping it opaque in the domain means the
// canvas can persist new control-library settings without taking a reference
// to the WPF control package.
public sealed record ManualWidgetDefinition(
    string Id,
    string Kind,
    string Name,
    string Unit,
    GridBounds Bounds,
    bool IsInput = false,
    string ConfigurationJson = "{}");
public sealed class ManualCanvasDefinition
{
    public int Version { get; set; } = 1;
    public string? WidgetLibraryBaseline { get; set; }
    public int GridSize { get; set; } = 16;
    public List<ManualWidgetDefinition> Widgets { get; set; } = [];
}
