using System.Windows.Controls;
using TestStation.App.ViewModels.Protocols;
namespace TestStation.App.Views.Protocols;
public partial class ProtocolTestView : UserControl
{
    public ProtocolTestView(ProtocolTestViewModel model) { InitializeComponent(); DataContext = model; }
}
