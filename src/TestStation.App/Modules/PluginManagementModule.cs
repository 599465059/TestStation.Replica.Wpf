using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.ViewModels;
using TestStation.App.Views;

namespace TestStation.App.Modules;

public sealed class PluginManagementModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<PluginManagementViewModel>();
        registry.RegisterSingleton<PluginManagementView>();
        registry.RegisterSingleton<DeviceTestingView>();
    }
    public void OnInitialized(IContainerProvider provider) { }
}
