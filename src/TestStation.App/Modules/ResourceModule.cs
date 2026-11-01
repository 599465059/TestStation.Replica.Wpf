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
using TestStation.Application.Devices;
using TestStation.App.Services;

namespace TestStation.App.Modules;

public sealed class ResourceModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IResourceCatalogRepository, ResourceCatalogRepository>();
        registry.RegisterSingleton<IResourceCatalogService, ResourceCatalogService>();
        registry.RegisterSingleton<IResourceCatalogEditingService, ResourceCatalogEditingService>();
        registry.RegisterSingleton<IResourceManager>(provider => new ResourceManager(provider.Resolve<IResourceCatalogService>(), [provider.Resolve<PcanVendorAdapter>()]));
        registry.RegisterSingleton<IResourceConfigurationService, ResourceConfigurationService>();
        registry.RegisterSingleton<ITransportDiscoveryService>(provider => new TransportDiscoveryService([provider.Resolve<PcanVendorAdapter>()]));
        registry.RegisterSingleton<ITransportDiagnosticService, TransportDiagnosticService>();
        registry.RegisterSingleton<IResourceEditorService, ResourceEditorService>();
        registry.RegisterSingleton<IDeviceManagementService>(provider => new DeviceManagementService([
            provider.Resolve<PowerSupplyManagementAdapter>(), provider.Resolve<HiokiManagementAdapter>()]));
        registry.RegisterSingleton<DeviceNavigationService>();
        registry.RegisterSingleton<DeviceManagementViewModel>();
        registry.RegisterSingleton<DeviceManagementView>();
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
