using TestStation.App.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TestStation.App.Controls;
using TestStation.App.ViewModels.Protocols;
using TestStation.App.Services;
namespace TestStation.App.Views.Protocols;
public partial class ProtocolEditorView : UserControl
{
    private Point _palettePress;
    private ProtocolFieldRole? _paletteRole;
    private ProtocolEditorViewModel Model => (ProtocolEditorViewModel)DataContext;
    private readonly IProtocolDialogService _dialogs;
    public ProtocolEditorView(ProtocolEditorViewModel model, IProtocolDialogService dialogs) { InitializeComponent(); DataContext = model; _dialogs = dialogs; }
    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        Model.SaveProtocolCommand.Execute(null);
        if (Model.Status == "协议已保存") await _dialogs.ShowInfoAsync(this, "保存协议", "协议已保存到当前工程。");
    }
    private async void NewProtocol_Click(object sender, RoutedEventArgs args)
    {
        if (await _dialogs.ConfirmAsync(this, UiText.NewProtocol, UiText.NewProtocolPrompt))
            Model.Catalog.NewProtocolCommand.Execute(null);
    }
    private async void DeleteProtocol_Click(object sender, RoutedEventArgs args)
    {
        var protocol = Model.Catalog.SelectedProtocol;
        if (protocol is null) return;
        if (await _dialogs.ConfirmAsync(this, UiText.DeleteProtocol, UiText.Format(nameof(UiText.DeleteProtocolPrompt), protocol.Name)))
            Model.Catalog.DeleteProtocolCommand.Execute(null);
    }
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> args)
    {
        if (DataContext is not ProtocolEditorViewModel) return;
        if (args.NewValue is ProtocolTreeRoot) Model.ShowFrame(null);
        else if (args.NewValue is ProtocolTreeEntry entry) Model.ShowFrame(entry.FrameId);
    }
    private void Palette_MouseDown(object sender, MouseButtonEventArgs args)
    {
        _palettePress = args.GetPosition(FieldPalette);
        _paletteRole = Ancestor<ListBoxItem>(args.OriginalSource as DependencyObject)?.DataContext as ProtocolFieldRole;
    }
    private void Palette_MouseMove(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || _paletteRole is null) return;
        var delta = args.GetPosition(FieldPalette) - _palettePress;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var role = _paletteRole.Role; _paletteRole = null;
        DragDrop.DoDragDrop(FieldPalette, new DataObject(ProtocolFrameCanvas.RoleFormat, role), DragDropEffects.Copy);
    }
    private void Palette_DoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (Ancestor<ListBoxItem>(args.OriginalSource as DependencyObject)?.DataContext is ProtocolFieldRole role) { Model.AddField(role.Role); args.Handled = true; }
    }
    private void Canvas_Loaded(object sender, RoutedEventArgs args) => ((ProtocolFrameCanvas)sender).CanMove = Model.CanMoveField;
    private void Canvas_Selected(object? sender, ProtocolCanvasSelectionEventArgs args)
    {
        Model.SelectField(args.FrameId, args.FieldId, args.Offset);
        if (args.FieldId is not null) PropertyTabs.SelectedIndex = 1;
    }
    private void Canvas_FieldDropped(object? sender, ProtocolCanvasSelectionEventArgs args) => Model.AddField(args.FieldId!, args.FrameId, args.Offset);
    private void Canvas_FieldMoved(object? sender, ProtocolCanvasSelectionEventArgs args) => Model.MoveField(args.FrameId, args.FieldId!, args.Offset);
    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T result) return result; node = VisualTreeHelper.GetParent(node); } return null;
    }
}
