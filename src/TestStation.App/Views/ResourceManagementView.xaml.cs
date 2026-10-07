using System.Windows.Controls;

namespace TestStation.App.Views;

public partial class ResourceManagementView : UserControl
{
    public ResourceManagementView(ViewModels.ResourceManagementViewModel model)
    {
        InitializeComponent(); DataContext = model;
    }
}
