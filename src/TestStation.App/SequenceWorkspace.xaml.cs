using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.ComponentModel;
using System.Windows.Threading;
using TestStation.App.ViewModels;
using TestStation.Domain;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using TestStation.App.Services;
using TestStation.App.Localization;

namespace TestStation.App;

public partial class SequenceWorkspace : UserControl
{
    private string? _defaultLayout;
    private readonly Dictionary<string, object> _panelContents = [];
    private MainViewModel? _executionModel;
    private bool _executionScrollPending;

    public SequenceWorkspace()
    {
        InitializeComponent();
        BindDocumentTitle();
        Loaded += (_, _) => ObserveExecutionModel();
        Unloaded += (_, _) => ObserveExecutionModel(null);
        DataContextChanged += (_, _) => { if (IsLoaded) ObserveExecutionModel(); };
    }

    private void ObserveExecutionModel() => ObserveExecutionModel(DataContext as MainViewModel);

    private void ObserveExecutionModel(MainViewModel? model)
    {
        if (_executionModel is not null) _executionModel.PropertyChanged -= ExecutionModelChanged;
        _executionModel = model;
        if (model is not null) model.PropertyChanged += ExecutionModelChanged;
        QueueExecutionScroll();
    }

    private void ExecutionModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CurrentExecutionRow)) QueueExecutionScroll();
    }

    private void QueueExecutionScroll()
    {
        if (_executionScrollPending || !IsLoaded) return;
        _executionScrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _executionScrollPending = false;
            if (IsLoaded && _executionModel?.CurrentExecutionRow is { IsCurrent: true } row && StepGrid.Items.Contains(row))
                StepGrid.ScrollIntoView(row);
        }));
    }

    private void OpenSignalChannelPicker(object sender, RoutedEventArgs args)
    {
        if (DataContext is not MainViewModel { CanEditSequence: true, IsSignalTableStep: true } model) return;
        var sequence = model.SelectedSequence;
        var step = model.SelectedRow?.Step;
        var dialog = new Views.SignalChannelPickerDialog(model.SignalChannelDefinitions, model.SignalValueRows.Select(row => row.Target))
        {
            Owner = Window.GetWindow(this)
        };
        if (dialog.ShowDialog() == true && ReferenceEquals(model.SelectedSequence, sequence) && ReferenceEquals(model.SelectedRow?.Step, step))
            model.AddSignalRows(dialog.SelectedChannels);
    }

    private async void SequenceListRightClick(object sender, MouseButtonEventArgs args)
    {
        if (DataContext is not MainViewModel model) return;
        var item = FindParent<ListBoxItem>(args.OriginalSource as DependencyObject);
        if (item?.DataContext is SequenceDefinition sequence)
        {
            if (!await model.SelectSequenceAsync(sequence)) { args.Handled = true; return; }
            item.Focus();
        }
    }

    private void VariableEditorExpanded(object sender, RoutedEventArgs args) => ResetVariableEditorHeight(sender);
    private void VariableEditorCollapsed(object sender, RoutedEventArgs args) => ResetVariableEditorHeight(sender);
    private static void ResetVariableEditorHeight(object sender)
    {
        if (sender is Expander { Parent: Grid grid }) grid.RowDefinitions[3].Height = GridLength.Auto;
    }

    private void VariableGridRightClick(object sender, MouseButtonEventArgs args)
    {
        if (DataContext is not MainViewModel model) return;
        var row = FindParent<DataGridRow>(args.OriginalSource as DependencyObject);
        if (row?.Item is SequenceVariable variable)
        {
            model.SelectedVariable = variable;
            if (!ReferenceEquals(model.SelectedVariable, variable)) { args.Handled = true; return; }
            row.Focus();
        }
    }

    private void SequenceMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (DataContext is not MainViewModel model) { args.Handled = true; return; }
        var item = FindParent<ListBoxItem>(args.OriginalSource as DependencyObject);
        if (item?.DataContext is SequenceDefinition sequence && !ReferenceEquals(sequence, model.SelectedSequence))
        { args.Handled = true; return; }
        EnableSelectedActions(SequenceList.ContextMenu, args.CursorLeft < 0 ? model.SelectedSequence is not null : item is not null);
    }

    private void VariableMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (DataContext is not MainViewModel model) { args.Handled = true; return; }
        var row = FindParent<DataGridRow>(args.OriginalSource as DependencyObject);
        if (row?.Item is SequenceVariable variable && !ReferenceEquals(variable, model.SelectedVariable))
        { args.Handled = true; return; }
        EnableSelectedActions(VariableGrid.ContextMenu, args.CursorLeft < 0 ? model.SelectedVariable is not null : row is not null);
    }

    private static void EnableSelectedActions(ContextMenu? menu, bool hasTarget)
    {
        if (menu is null) return;
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "SelectedItem"))) item.IsEnabled = hasTarget;
    }

    private static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void BindDocumentTitle()
    {
        // Layout models do not inherit the visual tree's DataContext.
        var panel = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Steps");
        BindingOperations.SetBinding(panel, LayoutContent.TitleProperty, new Binding("DataContext.SequenceEditorTitle")
        {
            Source = this, FallbackValue = UiText.SequenceEditor, TargetNullValue = UiText.SequenceEditor
        });
    }

    public void Configure(ISequenceInteractionService interactions)
    {
        interactions.Attach(this);
        foreach (var panel in DockManager.Layout.Descendents().OfType<LayoutAnchorable>())
            _panelContents[panel.ContentId] = panel.Content;
        SaveDefaultLayout();
    }

    private void SaveDefaultLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(DockManager).Serialize(writer);
        _defaultLayout = writer.ToString();
    }

    public void RestoreDefaultLayout()
    {
        if (_defaultLayout is null) return;
        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (_panelContents.TryGetValue(args.Model.ContentId, out var content)) args.Content = content;
            else args.Cancel = true;
        };
        serializer.Deserialize(new StringReader(_defaultLayout));
        BindDocumentTitle();
    }
}
