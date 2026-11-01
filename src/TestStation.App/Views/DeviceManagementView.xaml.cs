using System.Windows.Controls;
using TestStation.App.ViewModels;
namespace TestStation.App.Views;
public partial class DeviceManagementView : UserControl
{
    public DeviceManagementViewModel Model { get; }
    public DeviceManagementView(DeviceManagementViewModel model) { Model = model; InitializeComponent(); DataContext = model; }
}
