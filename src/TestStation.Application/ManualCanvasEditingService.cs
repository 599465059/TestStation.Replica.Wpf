using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public enum CanvasGesture { Move, NorthWest, North, NorthEast, West, East, SouthWest, South, SouthEast }
public interface IManualCanvasEditingService
{
    GridBounds Preview(GridBounds origin, CanvasGesture gesture, double dx, double dy, int gridSize,
        int minimumColumns = 10, int minimumRows = 2);
    void Validate(ManualCanvasDefinition document);
}
public interface IManualCanvasRepository
{
    ManualCanvasDefinition? Load(string projectRoot, string documentId = "main");
    void Save(string projectRoot, ManualCanvasDefinition document, string documentId = "main");
}

public sealed class ManualCanvasEditingService : IManualCanvasEditingService
{
    public const int MinimumColumns = 10;
    public const int MinimumRows = 2;
    public GridBounds Preview(GridBounds origin, CanvasGesture gesture, double dx, double dy, int gridSize,
        int minimumColumns = MinimumColumns, int minimumRows = MinimumRows)
    {
        if (gridSize is < 4 or > 64 || !double.IsFinite(dx) || !double.IsFinite(dy))
            throw new ArgumentException("Invalid canvas gesture.");
        var x = (int)Math.Clamp(Math.Round(dx / gridSize, MidpointRounding.AwayFromZero), -10000, 10000);
        var y = (int)Math.Clamp(Math.Round(dy / gridSize, MidpointRounding.AwayFromZero), -10000, 10000);
        if (gesture == CanvasGesture.Move)
            return origin with { Column = Math.Clamp(origin.Column + x, 0, 10000 - origin.Columns), Row = Math.Clamp(origin.Row + y, 0, 10000 - origin.Rows) };
        int left = origin.Column, top = origin.Row, right = left + origin.Columns, bottom = top + origin.Rows;
        // Legacy documents may already be smaller than new library minima.
        // Never jump their dimensions when the user starts a gesture.
        minimumColumns = Math.Min(origin.Columns, Math.Max(MinimumColumns, minimumColumns));
        minimumRows = Math.Min(origin.Rows, Math.Max(MinimumRows, minimumRows));
        if (gesture is CanvasGesture.NorthWest or CanvasGesture.West or CanvasGesture.SouthWest)
            left = Math.Clamp(left + x, 0, right - minimumColumns);
        if (gesture is CanvasGesture.NorthEast or CanvasGesture.East or CanvasGesture.SouthEast)
            right = Math.Clamp(right + x, left + minimumColumns, 10000);
        if (gesture is CanvasGesture.NorthWest or CanvasGesture.North or CanvasGesture.NorthEast)
            top = Math.Clamp(top + y, 0, bottom - minimumRows);
        if (gesture is CanvasGesture.SouthWest or CanvasGesture.South or CanvasGesture.SouthEast)
            bottom = Math.Clamp(bottom + y, top + minimumRows, 10000);
        return new(left, top, right - left, bottom - top);
    }
    public void Validate(ManualCanvasDefinition document)
    {
        if (document.Version != 1 || document.GridSize is < 4 or > 64 || document.Widgets is null || document.Widgets.Count > 500)
            throw new InvalidDataException("Invalid manual layout format.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in document.Widgets)
        {
            if (widget is null || string.IsNullOrWhiteSpace(widget.Id) || !ids.Add(widget.Id) ||
                !ManualWidgetKinds.IsSupported(widget.Kind) ||
                widget.Name is null || widget.Unit is null || widget.Bounds.Column < 0 || widget.Bounds.Row < 0 ||
                string.IsNullOrWhiteSpace(widget.ConfigurationJson) || !IsJsonObject(widget.ConfigurationJson) ||
                widget.Bounds.Columns < MinimumColumns || widget.Bounds.Rows < MinimumRows ||
                (long)widget.Bounds.Column + widget.Bounds.Columns > 10000 || (long)widget.Bounds.Row + widget.Bounds.Rows > 10000)
                throw new InvalidDataException("Invalid manual widget or grid bounds.");
        }
    }

    private static bool IsJsonObject(string value)
    {
        try { return JsonDocument.Parse(value).RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }
}
