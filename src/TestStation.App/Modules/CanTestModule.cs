using Prism.Ioc;
using Prism.Modularity;
using TestStation.Application.Can;
using TestStation.App.ViewModels;
using TestStation.App.Views;

namespace TestStation.App.Modules;

public sealed class CanTestModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<ICanSimulationService, SimulatedCanService>();
        registry.RegisterSingleton<CanTestViewModel>();
        registry.RegisterSingleton<CanTestView>();
    }
    public void OnInitialized(IContainerProvider containerProvider) { }
}
