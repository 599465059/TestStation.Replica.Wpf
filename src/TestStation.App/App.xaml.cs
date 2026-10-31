using Prism.Ioc;
using System.IO;


namespace TestStation.App;

public partial class App : Prism.DryIoc.PrismApplication
{
    private readonly Modules.ShellModule _shellModule = new();
    public App()
    {
        Themes.ResourceBootstrap.Initialize(this);
        DispatcherUnhandledException += (_, args) => LogStartupException(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogStartupException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
    }

    private static void LogStartupException(Exception error)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "startup.log"), $"[{DateTimeOffset.Now:O}] {error}\r\n\r\n");
        }
        catch
        {
            // Diagnostics must never replace the original startup failure.
        }
    }

    protected override System.Windows.Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        _shellModule.RegisterTypes(containerRegistry);
        new Devices.ItechPowerSupply.DeviceFamilyModule().RegisterTypes(containerRegistry);
        new Devices.HiokiPowerAnalyzer.DeviceFamilyModule().RegisterTypes(containerRegistry);
        new Modules.ResourceModule().RegisterTypes(containerRegistry);
        new Modules.ManualTestModule().RegisterTypes(containerRegistry);
        new Modules.PluginManagementModule().RegisterTypes(containerRegistry);
    }
}

