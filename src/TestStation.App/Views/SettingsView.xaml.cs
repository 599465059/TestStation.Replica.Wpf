using System.Windows.Controls;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

/// <summary>Settings page with its own section navigation. Layout only; behavior is in <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsView : UserControl
{
    public SettingsViewModel Model { get; }

    public SettingsView(SettingsViewModel model)
    {
        Model = model;
        InitializeComponent();
        DataContext = model;
    }
}
