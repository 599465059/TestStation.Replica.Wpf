using System.Windows;
using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.Services;
using TestStation.App.Devices.HiokiPowerAnalyzer.ViewModels;
using TestStation.App.Devices.HiokiPowerAnalyzer.Views;
using TestStation.Application;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Application.Hardware;
using TestStation.Infrastructure.Devices.HiokiPowerAnalyzer;

namespace TestStation.App.Devices.HiokiPowerAnalyzer;

public sealed class DeviceFamilyModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IHiokiPowerAnalyzerDeviceService, HiokiPowerAnalyzerDeviceService>();
        registry.RegisterSingleton<HiokiPowerAnalyzerPanelProvider>();
    }

    public void OnInitialized(IContainerProvider provider) { }
}

public sealed class HiokiPowerAnalyzerPanelProvider(
    IHiokiPowerAnalyzerDeviceService devices,
    IResourceManager resources,
    ISequenceDialogService dialogs) : IManualPluginViewProvider
{
    public string Id => HiokiPowerAnalyzerCatalog.FamilyId;
    public string Name => "HIOKI PW6001 功率分析仪";
    public string Version => "1.0";

    public FrameworkElement CreateView(ManualPluginViewContext context)
    {
        var model = new HiokiPowerAnalyzerViewModel(context.Runtime, devices, resources, dialogs);
        return new HiokiPowerAnalyzerView(model);
    }
}
