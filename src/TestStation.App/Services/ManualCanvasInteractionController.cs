using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using TestStation.App.ViewModels;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.Services;

/// <summary>Owns WPF pointer/keyboard gestures for the manual canvas.</summary>
public sealed class ManualCanvasInteractionController
{
    private readonly ManualTestViewModel _model;
    private readonly Grid _surface;
    private readonly ItemsControl _items;
    private readonly Rectangle _selectionMarquee;
    private ManualWidgetViewModel? _dragged;
    private Point _anchor;
    private GridBounds _origin;
    private CanvasGesture _gesture;
    private bool _started;
    private Vector _pendingDelta;
    private bool _hasPendingPreview;
    private TranslateTransform? _dragTransform;
    private FrameworkElement? _dragContainer;
    private bool _selecting;
    private Point _selectionAnchor;
    private Rect _selectionRect;
    private ManualWidgetViewModel[] _selectionBefore = [];
    private bool _appendSelection;

    public bool IsManipulating => _started;

    public ManualCanvasInteractionController(ManualTestViewModel model, Grid surface, ItemsControl items, Rectangle selectionMarquee)
    {
        _model = model;
        _surface = surface;
        _items = items;
        _selectionMarquee = selectionMarquee;
    }

    public void OnPointerDown(MouseButtonEventArgs e)
    {
        if (!_model.IsDesignMode || e.ChangedButton != MouseButton.Left) return;
        var node = e.OriginalSource as DependencyObject;
        ManualWidgetViewModel? widget = null; _gesture = CanvasGesture.Move;
        var moveHandle = false;
        var interactivePart = false;
        while (node is not null && node != _surface)
        {
            if (node is FrameworkElement element)
            {
                if (node is ButtonBase or TextBoxBase or PasswordBox or Selector or Slider or Thumb or GridSplitter)
                    interactivePart = true;
                if (element.Tag is string tag)
                {
                    if (tag.StartsWith("Edge", StringComparison.Ordinal)) tag = tag[4..];
                    if (Enum.TryParse<CanvasGesture>(tag, out var handle)) _gesture = handle;
                }
                if (element.Tag is "MoveHandle") moveHandle = true;
                if (element.DataContext is ManualWidgetViewModel item) widget = item;
            }
            node = WalkParent(node);
        }
        if (widget is null) { BeginSelection(e); return; }
        _model.SetSelection([widget], (Keyboard.Modifiers & ModifierKeys.Control) != 0);
        var canMove = _gesture != CanvasGesture.Move || moveHandle || (widget.IsDisplay && !interactivePart);
        if (!canMove) return;

        _surface.Focus();
        _dragged = widget; _anchor = e.GetPosition(_surface); _origin = widget.Bounds;
        _pendingDelta = default; _hasPendingPreview = false; _started = false;
        _dragContainer = _items.ItemContainerGenerator.ContainerFromItem(widget) as FrameworkElement;
        if (_gesture == CanvasGesture.Move && _dragContainer is not null)
        {
            _dragTransform = new TranslateTransform();
            _dragContainer.RenderTransform = _dragTransform;
            _dragContainer.RenderTransformOrigin = new Point(0, 0);
        }
        if (!_surface.CaptureMouse()) { _dragged = null; ClearDragTransform(); return; }
        CompositionTarget.Rendering += OnRendering;
        e.Handled = true;
    }

    public void OnPointerMove(MouseEventArgs e)
    {
        if (_selecting)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndSelection(true); return; }
            _selectionRect = new Rect(_selectionAnchor, e.GetPosition(_surface));
            Canvas.SetLeft(_selectionMarquee, _selectionRect.Left); Canvas.SetTop(_selectionMarquee, _selectionRect.Top);
            _selectionMarquee.Width = _selectionRect.Width; _selectionMarquee.Height = _selectionRect.Height;
            _selectionMarquee.Visibility = Visibility.Visible; e.Handled = true; return;
        }
        if (_dragged is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndGesture(true); return; }
        _pendingDelta = e.GetPosition(_surface) - _anchor; _hasPendingPreview = true; e.Handled = true;
    }

    public void OnPointerUp(MouseButtonEventArgs e)
    {
        if (_selecting && e.ChangedButton == MouseButton.Left)
        {
            _selectionRect = new Rect(_selectionAnchor, e.GetPosition(_surface)); EndSelection(false); e.Handled = true; return;
        }
        if (_dragged is null) return;
        _pendingDelta = e.GetPosition(_surface) - _anchor; _hasPendingPreview = true; EndGesture(false); e.Handled = true;
    }

    public void OnCaptureLost()
    {
        if (_surface.IsMouseCaptured) return;
        if (_selecting) EndSelection(true);
        if (_dragged is not null) EndGesture(true);
    }

    public void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _selecting) { EndSelection(true); e.Handled = true; return; }
        if (e.Key == Key.Escape && _dragged is not null) { EndGesture(true); e.Handled = true; return; }
        if (_dragged is not null || _selecting || !_model.IsDesignMode) return;
        if (Keyboard.FocusedElement is PasswordBox || Ancestor<TextBoxBase>(Keyboard.FocusedElement as DependencyObject) is { IsReadOnly: false }) return;
        if (!_surface.IsKeyboardFocusWithin) return;
        switch (e.Key)
        {
            case Key.Delete: _model.DeleteWidgetCommand.Execute(null); break;
            case Key.Escape: _model.ClearSelection(); _model.ForgetInteraction(); break;
            case Key.Left: _model.Nudge(-1, 0); break;
            case Key.Right: _model.Nudge(1, 0); break;
            case Key.Up: _model.Nudge(0, -1); break;
            case Key.Down: _model.Nudge(0, 1); break;
            default: return;
        }
        e.Handled = true;
    }

    public void Cancel()
    {
        EndSelection(true);
        EndGesture(true);
    }

    private void BeginSelection(MouseButtonEventArgs e)
    {
        _selectionAnchor = e.GetPosition(_surface); _selectionRect = new Rect(_selectionAnchor, _selectionAnchor);
        _selectionBefore = _model.SelectedWidgets.ToArray(); _appendSelection = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (!_appendSelection) { _model.ClearSelection(); _model.ForgetInteraction(); }
        _surface.Focus(); _selecting = _surface.CaptureMouse(); e.Handled = true;
    }

    private void EndSelection(bool cancel)
    {
        if (!_selecting) return;
        _selecting = false; _selectionMarquee.Visibility = Visibility.Collapsed;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        if (cancel) _model.SetSelection(_selectionBefore);
        else if (_selectionRect.Width >= 3 && _selectionRect.Height >= 3)
            _model.SelectInArea(_selectionRect.Left, _selectionRect.Top, _selectionRect.Right, _selectionRect.Bottom, _appendSelection);
        _selectionBefore = [];
    }

    private void EndGesture(bool cancel)
    {
        if (!cancel) ApplyPendingPreview();
        CompositionTarget.Rendering -= OnRendering;
        var item = _dragged; var finalDelta = _pendingDelta; var wasMove = _gesture == CanvasGesture.Move && _started;
        ClearDragTransform(); _dragged = null; _started = false;
        if (_surface.IsMouseCaptured) _surface.ReleaseMouseCapture();
        if (!cancel && item is not null && wasMove) _model.Preview(item, _origin, CanvasGesture.Move, finalDelta.X, finalDelta.Y);
        if (item is not null) _model.CompleteGesture(item, _origin, cancel);
    }

    private void OnRendering(object? sender, EventArgs e) => ApplyPendingPreview();

    private void ApplyPendingPreview()
    {
        if (_dragged is null || !_hasPendingPreview) return;
        var delta = _pendingDelta; _hasPendingPreview = false;
        if (!_started && delta.Length < 3) return;
        _started = true;
        if (_gesture == CanvasGesture.Move && _dragTransform is not null)
        {
            var bounds = _model.CalculatePreview(_origin, _gesture, delta.X, delta.Y, _dragged);
            _dragTransform.X = (bounds.Column - _origin.Column) * _model.GridSize;
            _dragTransform.Y = (bounds.Row - _origin.Row) * _model.GridSize; return;
        }
        _model.Preview(_dragged, _origin, _gesture, delta.X, delta.Y);
    }

    private void ClearDragTransform()
    {
        if (_dragContainer is not null) _dragContainer.RenderTransform = null;
        _dragContainer = null; _dragTransform = null;
    }

    private static DependencyObject? WalkParent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node)
    };

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T target) return target; node = WalkParent(node); }
        return null;
    }
}
