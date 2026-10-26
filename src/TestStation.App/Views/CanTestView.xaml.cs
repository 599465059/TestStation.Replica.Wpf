using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TestStation.App.ViewModels;
using TestStation.Domain.Can;

namespace TestStation.App.Views;

public partial class CanTestView : UserControl, IDisposable
{
    public CanTestViewModel Model { get; }
    private ContentPresenter? _dragPresenter;
    private string? _resizeEdge;
    private CanCanvasWidget? _resizeWidget;
    private Point _dragStart;
    private double _dragLeft;
    private double _dragTop;
    public CanTestView(CanTestViewModel model)
    {
        Model = model; InitializeComponent(); DataContext = model;
        Model.WidgetRequested += OnWidgetRequested;
    }
    public void OpenProject(string root) => Model.OpenProject(root);
    private void OnWidgetRequested(object? sender, string kind) { Model.AddWidget(kind); Model.Status = $"已添加控件：{kind}"; }
    private void Widget_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        var presenter = FindParent<ContentPresenter>(source);
        if (presenter?.DataContext is not CanCanvasWidget widget || !Model.IsDesignMode) return;
        Model.SelectWidget(widget);
        var tag = FindTag(source);
        if (tag?.StartsWith("CanResize", StringComparison.Ordinal) == true)
        {
            _resizeEdge = tag[9..]; _resizeWidget = widget; _dragStart = e.GetPosition(CanvasWidgets); presenter.CaptureMouse(); e.Handled = true; return;
        }
        if (!IsWidgetMoveHandle(source)) return;
        _dragPresenter = presenter; _dragStart = e.GetPosition(CanvasWidgets);
        _dragLeft = Canvas.GetLeft(presenter); _dragTop = Canvas.GetTop(presenter);
        presenter.CaptureMouse(); e.Handled = true;
    }
    private void Widget_MouseMove(object sender, MouseEventArgs e)
    {
        if (_resizeWidget is not null && e.LeftButton == MouseButtonState.Pressed)
        {
            var resizePoint = e.GetPosition(CanvasWidgets); Model.ResizeWidget(_resizeWidget, _resizeEdge!, resizePoint.X - _dragStart.X, resizePoint.Y - _dragStart.Y); _dragStart = resizePoint; return;
        }
        if (_dragPresenter is null || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(CanvasWidgets);
        Canvas.SetLeft(_dragPresenter, Math.Max(0, _dragLeft + point.X - _dragStart.X));
        Canvas.SetTop(_dragPresenter, Math.Max(0, _dragTop + point.Y - _dragStart.Y));
    }
    private void Widget_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeWidget is not null)
        {
            _resizeWidget = null; _resizeEdge = null; var presenter = FindParent<ContentPresenter>(e.OriginalSource as DependencyObject); presenter?.ReleaseMouseCapture(); e.Handled = true; return;
        }
        if (_dragPresenter is null) return;
        var widget = _dragPresenter.DataContext as CanCanvasWidget;
        if (widget is not null) { widget.Left = (int)Math.Round(Canvas.GetLeft(_dragPresenter)); widget.Top = (int)Math.Round(Canvas.GetTop(_dragPresenter)); }
        _dragPresenter.ReleaseMouseCapture(); _dragPresenter = null; e.Handled = true;
    }
    private void MessageTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => Model.SelectTreeNode(e.NewValue as CanTreeNode);
    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T match) return match; node = node is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(node); }
        return null;
    }
    private static bool IsWidgetMoveHandle(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement element && element.Tag is "CanWidgetMoveHandle") return true;
            node = node is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    private static string? FindTag(DependencyObject? node)
    {
        while (node is not null)
        {
            if (node is FrameworkElement element && element.Tag is string tag) return tag;
            node = node is Visual visual ? VisualTreeHelper.GetParent(visual) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
    private void DeleteWidgetMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Parent is ContextMenu menu && menu.DataContext is CanCanvasWidget widget)
            Model.DeleteWidgetCommand.Execute(widget);
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && Model.IsDesignMode)
        {
            var widget = Model.Widgets.FirstOrDefault(item => item.IsSelected);
            if (widget is not null) Model.DeleteWidgetCommand.Execute(widget);
            e.Handled = true;
        }
    }
    public void Dispose() { Model.WidgetRequested -= OnWidgetRequested; Model.Dispose(); }
}
