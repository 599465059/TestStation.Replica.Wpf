using System.IO;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Infrastructure;

namespace TestStation.Smoke;

internal static class ShellSmokeFixture
{
    // Use the production Settings -> ShellNavigationService route without writing user preferences.
    public static MainWindow Create(MainViewModel model, ThemeService theme)
    {
        var navigation = new ShellNavigationService();
        var settings = new StationSettingsStore(Path.Combine(Path.GetTempPath(), "test-station-ui-settings",
            Guid.NewGuid().ToString("N"), "settings.json"));
        var settingsView = new SettingsView(new SettingsViewModel(settings, theme, navigation));
        return new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), new ProtocolDialogService(),
            ManualCanvasRegression.CreateView(), shellNavigation: navigation, settingsView: settingsView, settings: settings);
    }
}
