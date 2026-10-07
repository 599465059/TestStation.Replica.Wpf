using Prism.Ioc;
using Prism.Modularity;
using TestStation.Application.Drivers;
using TestStation.Application.Resources;
using TestStation.Infrastructure.Resources;
using TestStation.App.ViewModels;
using TestStation.App.Views;

namespace TestStation.App.Modules;

public sealed class ResourceModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IResourceCatalogRepository, ResourceCatalogRepository>();
        registry.RegisterSingleton<IResourceCatalogService, ResourceCatalogService>();
        registry.RegisterSingleton<IResourceCatalogEditingService, ResourceCatalogEditingService>();
        registry.RegisterSingleton<IDeviceDriverFactory, SimulatedDeviceDriverFactory>();
        registry.RegisterSingleton<ITransportFactory, SimulatedTransportFactory>();
        registry.RegisterSingleton<IDriverCatalog, BuiltInDriverCatalog>();
        registry.RegisterSingleton<IChannelService, SimulatedChannelService>();
        registry.RegisterSingleton<IPollingService, PollingService>();
        registry.RegisterSingleton<ResourceManagementViewModel>();
        registry.RegisterSingleton<ResourceManagementView>();
    }

    public void OnInitialized(IContainerProvider containerProvider) { }
}
