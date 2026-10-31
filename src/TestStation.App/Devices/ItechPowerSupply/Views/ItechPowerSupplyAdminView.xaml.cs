using System.Windows.Controls;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
namespace TestStation.App.Devices.ItechPowerSupply.Views;
public partial class ItechPowerSupplyAdminView : UserControl
{
    public ItechPowerSupplyAdminViewModel Model { get; }
    public ItechPowerSupplyAdminView(ItechPowerSupplyAdminViewModel model) { InitializeComponent(); Model = model; DataContext = model; }
    private void ModelBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    { if (e.Column.Header?.ToString() == "标识" && e.Row.Item is PowerSupplyModel m && Model.ExistingModelIds.Contains(m.Id)) e.Cancel = true; }
    private void ChannelBeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    { if (e.Column.Header?.ToString() == "通道标识" && e.Row.Item is PowerSupplyChannel c && Model.ExistingChannelIds.Contains(c.Id)) e.Cancel = true; }
    private void Edited(object sender, DataGridCellEditEndingEventArgs e) => Dispatcher.BeginInvoke(Model.NotifyDraft);
    public bool CommitEdits()
    {
        foreach (var grid in FindGrids(this)) if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true)) return false;
        return true;
    }
    private static IEnumerable<DataGrid> FindGrids(System.Windows.DependencyObject parent)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); if (child is DataGrid grid) yield return grid; else foreach (var nested in FindGrids(child)) yield return nested; }
    }
}
