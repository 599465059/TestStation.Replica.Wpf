using TestStation.Domain;

namespace TestStation.Application;

/// <summary>
/// Owns mutations of the persisted manual-canvas document. The WPF view model
/// keeps selection and presentation state, while this service owns document
/// invariants and replacement of widget records.
/// </summary>
public interface IManualCanvasDocumentService
{
    void AddWidget(ManualCanvasDefinition document, ManualWidgetDefinition widget);
    bool UpdateBounds(ManualCanvasDefinition document, string widgetId, GridBounds bounds);
    bool UpdateWidget(ManualCanvasDefinition document, ManualWidgetDefinition widget);
    int RemoveWidgets(ManualCanvasDefinition document, IEnumerable<string> widgetIds);
}

public sealed class ManualCanvasDocumentService : IManualCanvasDocumentService
{
    public void AddWidget(ManualCanvasDefinition document, ManualWidgetDefinition widget)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(widget);
        if (document.Widgets.Any(item => item.Id == widget.Id))
            throw new ArgumentException("Manual widget id already exists.", nameof(widget));
        ValidateWidget(widget);
        if (document.Widgets.Count >= 500)
            throw new InvalidOperationException("The manual canvas cannot contain more than 500 widgets.");
        document.Widgets.Add(widget);
    }

    public bool UpdateBounds(ManualCanvasDefinition document, string widgetId, GridBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(document);
        var index = document.Widgets.FindIndex(item => item.Id == widgetId);
        if (index < 0) return false;
        var current = document.Widgets[index];
        ValidateBounds(bounds);
        document.Widgets[index] = current with { Bounds = bounds };
        return true;
    }

    public bool UpdateWidget(ManualCanvasDefinition document, ManualWidgetDefinition widget)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(widget);
        var index = document.Widgets.FindIndex(item => item.Id == widget.Id);
        if (index < 0) return false;
        ValidateWidget(widget);
        document.Widgets[index] = widget;
        return true;
    }

    public int RemoveWidgets(ManualCanvasDefinition document, IEnumerable<string> widgetIds)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(widgetIds);
        var ids = widgetIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        return document.Widgets.RemoveAll(item => ids.Contains(item.Id));
    }

    private static void ValidateWidget(ManualWidgetDefinition widget)
    {
        if (string.IsNullOrWhiteSpace(widget.Id) || string.IsNullOrWhiteSpace(widget.Name) || widget.Unit is null ||
            !ManualWidgetKinds.IsSupported(widget.Kind) ||
            string.IsNullOrWhiteSpace(widget.ConfigurationJson) || !IsJsonObject(widget.ConfigurationJson))
            throw new ArgumentException("Invalid manual widget definition.", nameof(widget));
        ValidateBounds(widget.Bounds);
    }

    private static void ValidateBounds(GridBounds bounds)
    {
        if (bounds.Column < 0 || bounds.Row < 0 ||
            bounds.Columns < ManualCanvasEditingService.MinimumColumns ||
            bounds.Rows < ManualCanvasEditingService.MinimumRows ||
            (long)bounds.Column + bounds.Columns > 10000 ||
            (long)bounds.Row + bounds.Rows > 10000)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Manual widget bounds are outside the canvas grid.");
    }

    private static bool IsJsonObject(string value)
    {
        try { return System.Text.Json.JsonDocument.Parse(value).RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; }
        catch (System.Text.Json.JsonException) { return false; }
    }
}
