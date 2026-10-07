using Prism.Ioc;
using Prism.Modularity;
using TestStation.Application;
using TestStation.Infrastructure;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.App.Services;

namespace TestStation.App.Modules;
public sealed class ManualTestModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IManualCanvasEditingService, ManualCanvasEditingService>();
        registry.RegisterSingleton<IManualCanvasDocumentService, ManualCanvasDocumentService>();
        registry.RegisterSingleton<ManualWidgetConfigurationAdapter>();
        registry.RegisterSingleton<ManualInstrumentFactory>();
        registry.RegisterSingleton<IManualCanvasRepository, ManualCanvasRepository>();
        registry.RegisterSingleton<ManualTestViewModel>();
        registry.RegisterSingleton<ManualTestView>();
        registry.RegisterSingleton<IManualWorkspaceRepository, ManualWorkspaceRepository>();
        registry.Register<IManualWorkspaceDialogService>(provider => (IManualWorkspaceDialogService)provider.Resolve<IProtocolDialogService>());
        registry.RegisterSingleton<ManualWorkspaceViewModel>();
        registry.RegisterSingleton<ManualWorkspaceView>();
    }
    public void OnInitialized(IContainerProvider containerProvider) { }
}
