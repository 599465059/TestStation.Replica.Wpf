using TestStation.App.Localization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TestStation.App.Services;
using TestStation.App.ViewModels.Protocols;
namespace TestStation.App.Views.Protocols;
public partial class ProtocolLibraryView : UserControl
{
    private readonly IProtocolDialogService _dialogs;
    public ProtocolLibraryView(ProtocolLibraryViewModel model, IProtocolDialogService dialogs) { InitializeComponent(); DataContext = model; _dialogs = dialogs; }
    private ProtocolLibraryViewModel Model => (ProtocolLibraryViewModel)DataContext;
    private async void New_Click(object sender, RoutedEventArgs args)
    {
        if (await _dialogs.ConfirmAsync(this, UiText.NewProtocol, UiText.NewProtocolPrompt)) Model.NewProtocolCommand.Execute(null);
    }
    private async void Delete_Click(object sender, RoutedEventArgs args)
    {
        if (Model.SelectedProtocol is null) return;
        if (await _dialogs.ConfirmAsync(this, UiText.DeleteProtocol, UiText.Format(nameof(UiText.DeleteProtocolPrompt), Model.SelectedProtocol.Name))) Model.DeleteProtocolCommand.Execute(null);
    }
    private void Import_Click(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Title = UiText.ImportProtocol, Filter = UiText.ProtocolImportFilter };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) ((ProtocolLibraryViewModel)DataContext).Import(dialog.FileName);
    }
    private void Export_Click(object sender, RoutedEventArgs args)
    {
        var model = Model;
        if (model.SelectedProtocol is null) return;
        var dialog = new SaveFileDialog { Title = UiText.ExportProtocol, Filter = UiText.ProtocolFiles, FileName = model.SelectedProtocol.Id + ".tsproto" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) model.Export(dialog.FileName);
    }
}
