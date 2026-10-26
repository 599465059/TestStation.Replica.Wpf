using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Application;

namespace TestStation.App.Views;

public partial class SignalChannelPickerDialog : Window
{
    private readonly List<ChannelSelectionRow> _rows;
    private readonly System.ComponentModel.ICollectionView _view;
    public IReadOnlyList<string> SelectedChannels => _rows.Where(row => row.IsSelected && row.CanSelect).Select(row => row.Definition.Id).ToArray();

    public SignalChannelPickerDialog(IEnumerable<SequenceChannelDefinition> channels, IEnumerable<string> existing)
    {
        var added = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _rows = channels.Select(channel => new ChannelSelectionRow(channel, added.Contains(channel.Id))).ToList();
        _view = CollectionViewSource.GetDefaultView(_rows);
        InitializeComponent();
        ChannelGrid.ItemsSource = _view;
    }

    private void SearchChanged(object sender, TextChangedEventArgs args)
    {
        var query = SearchInput.Text.Trim();
        _view.Filter = item => item is ChannelSelectionRow row &&
            (row.Definition.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Definition.Source.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             row.Definition.DataType.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             Localization.UiDisplay.Option(row.Definition.DataType).Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void SelectVisible(object sender, RoutedEventArgs args)
    {
        foreach (var row in _view.Cast<ChannelSelectionRow>().Where(row => row.CanSelect)) row.IsSelected = true;
    }

    private void ClearSelection(object sender, RoutedEventArgs args)
    {
        foreach (var row in _rows) row.IsSelected = false;
    }

    private void ConfirmSelection(object sender, RoutedEventArgs args) => DialogResult = true;
}

public partial class ChannelSelectionRow(SequenceChannelDefinition definition, bool added) : ObservableObject
{
    public SequenceChannelDefinition Definition { get; } = definition;
    public bool CanSelect => !added;
    public string Note => added ? "已添加" : "";
    [ObservableProperty] private bool _isSelected;
}
