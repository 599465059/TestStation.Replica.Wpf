using System.Windows.Controls;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

/// <summary>Home page after a project is opened. Layout only; data and actions are in <see cref="WorkbenchViewModel"/>.</summary>
public partial class WorkbenchView : UserControl
{
    public WorkbenchViewModel Model { get; }

    public WorkbenchView(WorkbenchViewModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
    }
}
