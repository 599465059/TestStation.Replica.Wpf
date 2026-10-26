using Prism.Ioc;

namespace TestStation.App;

public partial class App : Prism.DryIoc.PrismApplication
{
    private readonly Modules.ShellModule _shellModule = new();
    public App() => Themes.ResourceBootstrap.Initialize(this);

    protected override System.Windows.Window CreateShell() => Container.Resolve<MainWindow>();

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        _shellModule.RegisterTypes(containerRegistry);
        new Modules.ManualTestModule().RegisterTypes(containerRegistry);
    }
}

