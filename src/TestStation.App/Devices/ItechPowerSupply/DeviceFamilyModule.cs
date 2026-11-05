using System.Windows;
using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
using TestStation.App.Devices.ItechPowerSupply.Views;
using TestStation.App.Services;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices;
using TestStation.Application.Resources;
using TestStation.Application.Hardware;
using TestStation.Infrastructure.Devices.ItechPowerSupply;

namespace TestStation.App.Devices.ItechPowerSupply;

public sealed class DeviceFamilyModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IItechPowerSupplyProfileStore, ItechPowerSupplyProfileStore>();
        registry.RegisterSingleton<ItechPowerSupplyDriverFactory>();
        registry.RegisterSingleton<IDeviceFamilyModule, ItechPowerSupplyFamilyModule>();
        registry.RegisterSingleton<IDeviceFamilyCatalog>(provider =>
        {
            var catalog = new DeviceFamilyCatalog();
            catalog.Register(provider.Resolve<IDeviceFamilyModule>().Definition);
            return catalog;
        });
        registry.RegisterSingleton<IPowerSupplyDeviceService, PowerSupplyDeviceService>();
        registry.RegisterSingleton<PowerSupplyManagementAdapter>();
        registry.RegisterSingleton<PowerSupplyPanelProvider>();
    }
    public void OnInitialized(IContainerProvider provider) { }
}

public sealed class PowerSupplyPanelProvider(IPowerSupplyDeviceService devices, IItechPowerSupplyProfileStore profiles,
    IResourceManager resources, ISequenceDialogService sequenceDialogs) : IManualPluginViewProvider, INavigationIconSource
{
    public string Id => "itech.power-supply";
    public string Name => "艾德克斯直流电源";
    public string Version => "2.0";
    public string NavigationIcon => "CurrentDc";
    public FrameworkElement CreateView(ManualPluginViewContext context)
    {
        var operation = new ItechPowerSupplyViewModel(context.Runtime, devices, profiles, resources, sequenceDialogs, context.ResourceEditor, context.DeviceId);
        var view = new ItechPowerSupplyView(operation);
        if (context.WorkspaceId != "device-testing") return view;
        var admin = new ItechPowerSupplyAdminViewModel(profiles, devices, context.Runtime, sequenceDialogs);
        return new ItechPowerSupplyFamilyView(view, new ItechPowerSupplyAdminView(admin), admin, sequenceDialogs);
    }
}
