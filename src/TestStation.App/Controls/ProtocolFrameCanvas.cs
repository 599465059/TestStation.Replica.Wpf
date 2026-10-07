using TestStation.App.Localization;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TestStation.Domain.Protocols;

namespace TestStation.App.Controls;

public sealed record ProtocolFieldMove(string FrameId, string FieldId);
public sealed class ProtocolCanvasSelectionEventArgs(string frameId, string? fieldId, int offset) : EventArgs
{
    public string FrameId { get; } = frameId;
    public string? FieldId { get; } = fieldId;
    public int Offset { get; } = offset;
}

// Domain-specific byte visualization; foundation controls continue to come from the package.
public sealed class ProtocolFrameCanvas : FrameworkElement
{
    public const string RoleFormat = "TestStation.ProtocolRole";
    public const string MoveFormat = "TestStation.ProtocolFieldMove";
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(nameof(Frame), typeof(ProtocolFrame), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ByteWidthProperty = DependencyProperty.Register(nameof(ByteWidth), typeof(double), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(54d, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty DisplayBytesProperty = DependencyProperty.Register(nameof(DisplayBytes), typeof(int), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ViewportWidthProperty = DependencyProperty.Register(nameof(ViewportWidth), typeof(double), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(800d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ViewportOffsetProperty = DependencyProperty.Register(nameof(ViewportOffset), typeof(double), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedFieldIdProperty = DependencyProperty.Register(nameof(SelectedFieldId), typeof(string), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectedFrameIdProperty = DependencyProperty.Register(nameof(SelectedFrameId), typeof(string), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(nameof(AccentBrush), typeof(Brush), typeof(ProtocolFrameCanvas), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public ProtocolFrame? Frame { get => (ProtocolFrame?)GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public double ByteWidth { get => (double)GetValue(ByteWidthProperty); set => SetValue(ByteWidthProperty, value); }
    public int DisplayBytes { get => (int)GetValue(DisplayBytesProperty); set => SetValue(DisplayBytesProperty, value); }
    public double ViewportWidth { get => (double)GetValue(ViewportWidthProperty); set => SetValue(ViewportWidthProperty, value); }
    public double ViewportOffset { get => (double)GetValue(ViewportOffsetProperty); set => SetValue(ViewportOffsetProperty, value); }
    public string? SelectedFieldId { get => (string?)GetValue(SelectedFieldIdProperty); set => SetValue(SelectedFieldIdProperty, value); }
    public string? SelectedFrameId { get => (string?)GetValue(SelectedFrameIdProperty); set => SetValue(SelectedFrameIdProperty, value); }
    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public event EventHandler<ProtocolCanvasSelectionEventArgs>? ByteSelected;
    public event EventHandler<ProtocolCanvasSelectionEventArgs>? FieldDropped;
    public event EventHandler<ProtocolCanvasSelectionEventArgs>? FieldMoved;
    public Func<string, string, int, bool>? CanMove { get; set; }
    private Point _press;
    private ProtocolField? _dragField;
    private int _selectedByte = -1;
    private int _dropByte = -1;
    private double CellWidth => double.IsFinite(ByteWidth) ? Math.Clamp(ByteWidth, 36, 160) : 54;
    private int Count => Math.Max(Frame?.CalculatedLength ?? 0, DisplayBytes > 0 ? Math.Min(DisplayBytes, 4096) : Math.Max(1, (int)(Math.Max(36, (double.IsFinite(ViewportWidth) ? ViewportWidth : 800) - 16) / CellWidth)));
    public ProtocolFrameCanvas()
    {
        AllowDrop = true; Focusable = true; SnapsToDevicePixels = true;
        SetResourceReference(TextBrushProperty, "TextBrush"); SetResourceReference(AccentBrushProperty, "AccentBrush");
        DragOver += OnDragOver; Drop += OnDrop;
        DragLeave += (_, _) => { _dropByte = -1; InvalidateVisual(); };
    }
    protected override Size MeasureOverride(Size availableSize) => new(Math.Max(1, Count) * CellWidth, 104);
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var width = CellWidth;
        Brush Resource(string key) => TryFindResource(key) as Brush ?? TextBrush;
        var stroke = new Pen(Resource("BorderBrush"), 1);
        var first = Math.Max(0, (int)(ViewportOffset / width) - 1);
        var last = Math.Min(Count, first + Math.Max(1, (int)(Math.Max(36, ViewportWidth) / width)) + 3);
        for (var index = first; index < last; index++)
        {
            var rect = new Rect(index * width, 0, width, 96);
            drawing.DrawRectangle(Resource("SurfaceBrush"), stroke, rect);
            DrawText(drawing, index.ToString(CultureInfo.InvariantCulture), new Point(rect.X + 7, 4), width - 12, Resource("MutedBrush"), 11);
        }
        foreach (var item in Frame?.Fields ?? [])
        {
            if (item.ByteOffset >= last || item.EndOffset <= first) continue;
            var rect = new Rect(item.ByteOffset * width + 2, 27, item.Length * width - 4, 57);
            var roleKey = $"Protocol{char.ToUpperInvariant(item.Role[0])}{item.Role[1..]}Brush";
            var brush = Resource(roleKey);
            drawing.PushOpacity(item.Enabled ? .16 : .06); drawing.DrawRoundedRectangle(brush, null, rect, 5, 5); drawing.Pop();
            var selected = item.Id == SelectedFieldId && Frame?.Id == SelectedFrameId;
            drawing.DrawRoundedRectangle(null, new Pen(selected ? AccentBrush : brush, selected ? 2 : 1), rect, 5, 5);
            DrawText(drawing, item.Name, new Point(rect.X + 5, rect.Y + 7), rect.Width - 10, TextBrush, 12);
            DrawText(drawing, $"{item.DataType} · {item.Length}B", new Point(rect.X + 5, rect.Y + 31), rect.Width - 10, Resource("MutedBrush"), 10);
        }
        if (_selectedByte >= 0 && _selectedByte < Count && SelectedFieldId is null && Frame?.Id == SelectedFrameId)
            drawing.DrawRectangle(null, new Pen(AccentBrush, 2), new Rect(_selectedByte * width + 2, 27, width - 4, 57));
        if (_dropByte >= 0) drawing.DrawLine(new Pen(AccentBrush, 2), new Point(_dropByte * width, 25), new Point(_dropByte * width, 90));
    }
    private void DrawText(DrawingContext drawing, string text, Point point, double width, Brush brush, double size)
    {
        if (width <= 0) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, width), MaxTextHeight = size * 1.5, Trimming = TextTrimming.CharacterEllipsis };
        drawing.PushClip(new RectangleGeometry(new Rect(point.X, point.Y, width, size * 1.6)));
        drawing.DrawText(formatted, point); drawing.Pop();
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        if (Frame is null) return;
        Focus(); _press = args.GetPosition(this); _selectedByte = Math.Clamp((int)(_press.X / CellWidth), 0, Count - 1);
        _dragField = Frame.Fields.FirstOrDefault(item => _selectedByte >= item.ByteOffset && _selectedByte < item.EndOffset);
        ByteSelected?.Invoke(this, new(Frame.Id, _dragField?.Id, _selectedByte));
        InvalidateVisual(); args.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        var point = args.GetPosition(this);
        var offset = Math.Max(0, (int)(point.X / CellWidth));
        var item = Frame?.Fields.FirstOrDefault(value => offset >= value.ByteOffset && offset < value.EndOffset);
        ToolTip = item is null ? $"字节 {offset}" : $"{item.Name} / {item.Id}\n{item.ByteOffset}..{item.EndOffset - 1} · {item.DataType} · {UiDisplay.Option(item.ByteOrder)}\n{item.Description}";
        if (args.LeftButton != MouseButtonState.Pressed || _dragField is null || Frame is null) return;
        if (Math.Abs(point.X - _press.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _press.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var move = new ProtocolFieldMove(Frame.Id, _dragField.Id); _dragField = null;
        DragDrop.DoDragDrop(this, new DataObject(MoveFormat, move), DragDropEffects.Move);
    }
    private void OnDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true; args.Effects = DragDropEffects.None; _dropByte = -1;
        if (Frame is null) return;
        var offset = Math.Max(0, (int)(args.GetPosition(this).X / CellWidth));
        if (args.Data.GetData(MoveFormat) is ProtocolFieldMove move && move.FrameId == Frame.Id && CanMove?.Invoke(move.FrameId, move.FieldId, offset) == true)
            args.Effects = DragDropEffects.Move;
        else if (args.Data.GetData(RoleFormat) is string && Frame.Fields.All(item => !item.Enabled || offset < item.ByteOffset || offset >= item.EndOffset))
            args.Effects = DragDropEffects.Copy;
        if (args.Effects != DragDropEffects.None) _dropByte = offset;
        InvalidateVisual();
    }
    private void OnDrop(object sender, DragEventArgs args)
    {
        var offset = _dropByte; _dropByte = -1; InvalidateVisual(); args.Handled = true;
        if (offset < 0 || Frame is null) return;
        if (args.Data.GetData(MoveFormat) is ProtocolFieldMove move) FieldMoved?.Invoke(this, new(Frame.Id, move.FieldId, offset));
        else if (args.Data.GetData(RoleFormat) is string role) FieldDropped?.Invoke(this, new(Frame.Id, role, offset));
    }
}
