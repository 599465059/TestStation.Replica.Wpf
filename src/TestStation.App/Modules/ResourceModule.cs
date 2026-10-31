using Prism.Ioc;
using Prism.Modularity;
using TestStation.Application.Drivers;
using TestStation.Application.Resources;
using TestStation.Infrastructure.Resources;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application.Hardware;
using TestStation.Infrastructure.Transports;
using TestStation.Infrastructure.Transports.Can.Pcan;

namespace TestStation.App.Modules;

public sealed class ResourceModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IResourceCatalogRepository, ResourceCatalogRepository>();
        registry.RegisterSingleton<IResourceCatalogService, ResourceCatalogService>();
        registry.RegisterSingleton<IResourceCatalogEditingService, ResourceCatalogEditingService>();
        registry.RegisterSingleton<IResourceManager, ResourceManager>();
        registry.RegisterSingleton<TestStation.Application.Drivers.IDeviceDriverFactory, SimulatedDeviceDriverFactory>();
        registry.RegisterSingleton<TestStation.Application.Hardware.IDeviceDriverFactory, LegacyDeviceDriverFactoryAdapter>();
        registry.RegisterSingleton<ITransportFactory, LegacyTransportFactoryAdapter>();
        registry.RegisterSingleton<ITransportSessionFactory, TransportSessionFactory>();
        registry.RegisterSingleton<PcanVendorAdapter>();
        registry.RegisterSingleton<ICanVendorAdapter>(provider => provider.Resolve<PcanVendorAdapter>());
        registry.RegisterSingleton<ICanHardwareDiscovery>(provider => provider.Resolve<PcanVendorAdapter>());
        registry.RegisterSingleton<IDriverCatalog, BuiltInDriverCatalog>();
        registry.RegisterSingleton<IChannelService, SimulatedChannelService>();
        registry.RegisterSingleton<IPollingService, PollingService>();
        registry.RegisterSingleton<ResourceManagementViewModel>();
        registry.RegisterSingleton<ResourceManagementView>();
    }

    public void OnInitialized(IContainerProvider containerProvider) { }
}
