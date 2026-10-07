using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using TestStation.App.ViewModels;
using TestStation.App.Localization;

namespace TestStation.App.Services;

public interface ISequenceInteractionService
{
    void Attach(SequenceWorkspace workspace);
}

// Adapted from the reference editor's interaction coordinator. This adapter owns
// pointer geometry only; the injected editing service owns all document changes.
public sealed class SequenceInteractionService(ISequenceDropTargetService targets) : ISequenceInteractionService
{
    private const string PaletteFormat = "TestStation.PaletteStep";
    private const string StepFormat = "TestStation.SequenceStep";
    private SequenceWorkspace _view = null!;
    private Point _paletteStart;
    private Point _stepStart;
    private StepPaletteItem? _paletteItem;
    private SequenceRow? _dragRow;
    private long _lastScroll;
    private MainViewModel? Model => _view.DataContext as MainViewModel;

    public void Attach(SequenceWorkspace workspace)
    {
        if (_view is not null) throw new InvalidOperationException("交互服务只能绑定一个编辑页面");
        _view = workspace;
        var grid = workspace.StepGrid;
        workspace.StepPalette.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _paletteStart = args.GetPosition(workspace.StepPalette);
            _paletteItem = Ancestor<TreeViewItem>(args.OriginalSource as DependencyObject)?.DataContext as StepPaletteItem;
        };
        workspace.StepPalette.MouseDoubleClick += (_, args) =>
        {
            if (Ancestor<TreeViewItem>(args.OriginalSource as DependencyObject)?.DataContext is StepPaletteItem item)
            {
                Model?.AddStep(item.Kind);
                args.Handled = true;
            }
        };
        workspace.StepPalette.PreviewMouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed || _paletteItem is null || Model?.CanEditSequence != true) return;
            if (!PastThreshold(args.GetPosition(workspace.StepPalette) - _paletteStart)) return;
            var item = _paletteItem;
            _paletteItem = null;
            DragDrop.DoDragDrop(workspace.StepPalette, new DataObject(PaletteFormat, item.Kind), DragDropEffects.Copy);
        };
        grid.PreviewMouseLeftButtonDown += (_, args) =>
        {
            _stepStart = args.GetPosition(grid);
            _dragRow = Ancestor<DataGridRow>(args.OriginalSource as DependencyObject)?.Item as SequenceRow;
            if (Ancestor<CheckBox>(args.OriginalSource as DependencyObject) is not null) _dragRow = null;
            if (_dragRow?.RowType == "group")
            {
                var section = _dragRow.Section;
                if (Model is { } model)
                {
                    model.ToggleGroup(section);
                    // The refreshed header remains the insertion target for a
                    // subsequent library double-click or clipboard paste.
                    grid.SelectedItem = model.Rows.First(row => row.RowType == "group" && row.Section == section);
                }
                _dragRow = null;
                args.Handled = true;
            }
        };
        grid.PreviewMouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed || _dragRow?.Step is not { } step || Model?.CanEditSequence != true) return;
            if (!PastThreshold(args.GetPosition(grid) - _stepStart)) return;
            _dragRow = null;
            DragDrop.DoDragDrop(grid, new DataObject(StepFormat, step.Id), DragDropEffects.Move);
        };
        grid.DragOver += OnDragOver;
        grid.Drop += OnDrop;
        grid.DragLeave += (_, _) => HideIndicator();
        grid.PreviewKeyDown += OnKeyDown;
        workspace.PreviewKeyDown += (_, args) =>
        {
            if (Model is null || Keyboard.Modifiers != ModifierKeys.Control) return;
            // Preserve native TextBox undo/redo while the user is typing.
            if (args.Key != Key.S && Ancestor<TextBox>(args.OriginalSource as DependencyObject) is not null) return;
            var command = args.Key switch {
                Key.Z => (System.Windows.Input.ICommand)Model.UndoSequenceCommand,
                Key.Y => Model.RedoSequenceCommand,
                Key.S => Model.SaveProjectCommand,
                _ => null
            };
            if (command is null) return;
            if (command.CanExecute(null)) command.Execute(null);
            args.Handled = true;
        };
        grid.PreviewMouseRightButtonDown += (_, args) =>
        {
            if (Ancestor<DataGridRow>(args.OriginalSource as DependencyObject) is { } row && !row.IsSelected)
            {
                grid.SelectedItems.Clear();
                row.IsSelected = true;
                Model!.SelectedRow = row.Item as SequenceRow;
            }
        };
        var menu = new ContextMenu();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "AppContextMenuStyle");
        menu.SetResourceReference(Control.BackgroundProperty, "MenuBackgroundBrush");
        menu.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        menu.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        AddMenu(menu, UiText.Delete, "Del", PackIconKind.DeleteOutline, () => Model?.DeleteSteps(Selection()));
        menu.Items.Add(new Separator());
        AddMenu(menu, UiText.MoveUp, "Ctrl+↑", PackIconKind.ArrowUp, () => Model?.MoveSelectedStep(-1));
        AddMenu(menu, UiText.MoveDown, "Ctrl+↓", PackIconKind.ArrowDown, () => Model?.MoveSelectedStep(1));
        menu.Items.Add(new Separator());
        AddMenu(menu, UiText.Copy, "Ctrl+C", PackIconKind.ContentCopy, () => Model?.CopySteps(Selection()));
        AddMenu(menu, UiText.Cut, "Ctrl+X", PackIconKind.ContentCut, () => Model?.CutSteps(Selection()));
        AddMenu(menu, UiText.Paste, "Ctrl+V", PackIconKind.ContentPaste, () => Model?.PasteSteps());
        menu.Opened += (_, _) =>
        {
            foreach (var item in menu.Items.OfType<MenuItem>())
                item.IsEnabled = item.Header?.ToString() == UiText.Copy
                    ? Model?.SelectedSequence is not null : Model?.CanEditSequence == true;
        };
        grid.ContextMenu = menu;
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true;
        args.Effects = DragDropEffects.None;
        HideIndicator();
        if (Model is null || !Model.CanEditSequence || !Supported(args.Data)) return;
        var position = args.GetPosition(_view.StepGrid);
        AutoScroll(position);
        if (Resolve(position) is not { } hit) return;
        if (args.Data.GetData(StepFormat) is string id && !Model.CanMoveStep(id, hit.Target.Target)) return;
        args.Effects = args.Data.GetDataPresent(PaletteFormat) ? DragDropEffects.Copy : DragDropEffects.Move;
        // Nesting changes the insertion target, not the visible line's width.
        // The overlay uses grid coordinates and stays full width when scrolling.
        _view.InsertionLine.X1 = 0;
        _view.InsertionLine.X2 = _view.StepGrid.ActualWidth;
        _view.InsertionLine.Y1 = _view.InsertionLine.Y2 = hit.Y;
        _view.InsertionLine.Visibility = Visibility.Visible;
    }

    private void OnDrop(object sender, DragEventArgs args)
    {
        args.Handled = true;
        HideIndicator();
        if (Model is null || !Model.CanEditSequence || !Supported(args.Data)) return;
        if (Resolve(args.GetPosition(_view.StepGrid)) is not { } hit) return;
        if (args.Data.GetData(PaletteFormat) is string kind) Model.AddStep(kind, hit.Target.Target);
        else if (args.Data.GetData(StepFormat) is string id && Model.CanMoveStep(id, hit.Target.Target))
            Model.MoveStep(id, hit.Target.Target);
    }

    private (SequenceDropTarget Target, double Y)? Resolve(Point position)
    {
        var grid = _view.StepGrid;
        var control = Ancestor<DataGridRow>(grid.InputHitTest(position) as DependencyObject);
        if (control?.Item is SequenceRow row)
        {
            var bounds = control.TransformToAncestor(grid).TransformBounds(new Rect(control.RenderSize));
            var fraction = (position.Y - bounds.Top) / Math.Max(1, bounds.Height);
            var target = targets.Resolve(row, fraction, false);
            var anchor = targets.CanonicalAnchor(grid.Items.Cast<SequenceRow>().ToArray(), target.Target);
            if (grid.ItemContainerGenerator.ContainerFromIndex(anchor.RowIndex) is DataGridRow anchorRow)
            {
                var anchorBounds = anchorRow.TransformToAncestor(grid).TransformBounds(new Rect(anchorRow.RenderSize));
                return (target, anchor.BelowRow ? anchorBounds.Bottom : anchorBounds.Top);
            }
            return (target, target.BelowRow ? bounds.Bottom : bounds.Top);
        }
        // A blank area below the final row belongs to Cleanup, not to Main.
        if (grid.Items.Count > 0 && grid.ItemContainerGenerator.ContainerFromIndex(grid.Items.Count - 1) is DataGridRow last)
        {
            var bounds = last.TransformToAncestor(grid).TransformBounds(new Rect(last.RenderSize));
            if (position.Y >= bounds.Bottom && last.Item is SequenceRow lastRow)
            {
                var target = targets.Resolve(lastRow, 1, false);
                var anchor = targets.CanonicalAnchor(grid.Items.Cast<SequenceRow>().ToArray(), target.Target);
                if (grid.ItemContainerGenerator.ContainerFromIndex(anchor.RowIndex) is DataGridRow anchorRow)
                {
                    var anchorBounds = anchorRow.TransformToAncestor(grid).TransformBounds(new Rect(anchorRow.RenderSize));
                    return (target, anchor.BelowRow ? anchorBounds.Bottom : anchorBounds.Top);
                }
                return (target, bounds.Bottom);
            }
        }
        return null;
    }

    private void AutoScroll(Point position)
    {
        var now = Environment.TickCount64;
        if (now - _lastScroll < 90) return;
        var scroll = Descendants<ScrollViewer>(_view.StepGrid).FirstOrDefault();
        if (scroll is null) return;
        if (position.Y < 45) scroll.LineUp();
        else if (position.Y > _view.StepGrid.ActualHeight - 30) scroll.LineDown();
        else return;
        _lastScroll = now;
    }

    private void OnKeyDown(object sender, KeyEventArgs args)
    {
        if (Model is null) return;
        if (args.Key == Key.Delete) Model.DeleteSteps(Selection());
        else if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (args.Key)
            {
                case Key.C: Model.CopySteps(Selection()); break;
                case Key.X: Model.CutSteps(Selection()); break;
                case Key.V: Model.PasteSteps(); break;
                case Key.Up: Model.MoveSelectedStep(-1); break;
                case Key.Down: Model.MoveSelectedStep(1); break;
                default: return;
            }
        }
        else return;
        args.Handled = true;
    }

    private List<SequenceRow> Selection() => _view.StepGrid.SelectedItems.OfType<SequenceRow>().ToList();
    private void HideIndicator() => _view.InsertionLine.Visibility = Visibility.Collapsed;
    private static bool Supported(IDataObject data) => data.GetDataPresent(PaletteFormat) || data.GetDataPresent(StepFormat);
    private static bool PastThreshold(Vector delta) => Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance || Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance;
    private static void AddMenu(ContextMenu menu, string title, string shortcut, PackIconKind icon, Action action)
    {
        var item = new MenuItem { Header = title, InputGestureText = shortcut, Icon = new PackIcon { Kind = icon, Width = 16, Height = 16 } };
        item.SetResourceReference(FrameworkElement.StyleProperty, "AppMenuItemStyle");
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
