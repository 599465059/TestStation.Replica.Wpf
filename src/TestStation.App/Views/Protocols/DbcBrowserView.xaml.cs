using TestStation.App.Localization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TestStation.App.ViewModels.Protocols;
namespace TestStation.App.Views.Protocols;
public partial class DbcBrowserView : UserControl
{
    public DbcBrowserView(DbcBrowserViewModel model) { InitializeComponent(); DataContext = model; }
    private void Import_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = UiText.ImportDbc, Filter = UiText.CanDatabaseFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ((DbcBrowserViewModel)DataContext).Import(dialog.FileName);
    }
    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> args)
    {
        if (DataContext is DbcBrowserViewModel model && args.NewValue is DbcTreeNode node) model.SelectedNode = node;
    }
}
