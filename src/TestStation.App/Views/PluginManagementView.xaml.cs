using System.Windows.Controls;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

public partial class PluginManagementView : UserControl
{
    public PluginManagementView(PluginManagementViewModel model)
    {
        InitializeComponent(); DataContext = model;
    }
}
