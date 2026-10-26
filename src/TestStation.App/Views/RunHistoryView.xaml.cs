using System.Windows.Controls;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

public partial class RunHistoryView : UserControl
{
    public RunHistoryViewModel Model { get; }
    public RunHistoryView(RunHistoryViewModel model) { Model = model; InitializeComponent(); DataContext = model; }
    public void OpenProject(string root) => Model.OpenProject(root);
}
