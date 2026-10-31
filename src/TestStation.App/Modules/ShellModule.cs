using TestStation.Infrastructure.Devices.ItechPowerSupply;
using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.ViewModels;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.App.Views;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices.HiokiPowerAnalyzer;

namespace TestStation.App.Modules;

public sealed class ShellModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<MainViewModel>();
        containerRegistry.RegisterSingleton<ControlPreviewViewModel>();
        containerRegistry.Register<IIndustrialControlsThemeAdapter, IndustrialControlsThemeAdapter>();
        containerRegistry.Register<ControlPreviewView>();
        containerRegistry.RegisterSingleton<RunHistoryViewModel>();
        containerRegistry.RegisterSingleton<RunHistoryView>();
        containerRegistry.RegisterSingleton<IThemeService, ThemeService>();

        containerRegistry.RegisterSingleton<IDeviceModelCatalog, TestStation.SimulatedPlugins.SimulatedDeviceModelCatalog>();
        containerRegistry.RegisterSingleton<IManualPluginViewRegistry>(provider =>
        {
            var registry = new ManualPluginViewRegistry(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "disabled-plugins.json"));
            new SimulatedPluginRegistration(registry, provider.Resolve<IDeviceModelCatalog>(), provider.Resolve<IItechPowerSupplyProfileStore>()).RegisterAll();
            registry.Register(provider.Resolve<TestStation.App.Devices.ItechPowerSupply.PowerSupplyPanelProvider>());
            registry.Register(provider.Resolve<TestStation.App.Devices.HiokiPowerAnalyzer.HiokiPowerAnalyzerPanelProvider>());
            var hioki = provider.Resolve<IHiokiPowerAnalyzerDeviceService>();
            hioki.FamilyEnabled = registry.IsEnabled(HiokiPowerAnalyzerCatalog.FamilyId);
            registry.Changed += (_, _) => hioki.FamilyEnabled = registry.IsEnabled(HiokiPowerAnalyzerCatalog.FamilyId);
            return registry;
        });
        containerRegistry.RegisterSingleton<IAutoTestDisplayService, AutoTestDisplayService>();
        containerRegistry.RegisterSingleton<ISequenceEditingService, SequenceEditingService>();
        containerRegistry.RegisterSingleton<ISequenceDocumentService, SequenceDocumentService>();
        containerRegistry.RegisterSingleton<IAutomaticWorkspaceService, AutomaticWorkspaceService>();
        containerRegistry.RegisterSingleton<ISequenceStepCatalog, BuiltInSequenceStepCatalog>();
        containerRegistry.RegisterSingleton<ISequenceChannelProvider>(provider => new SimulatedPluginChannelProvider(
            provider.Resolve<IManualPluginViewRegistry>(), provider.Resolve<IDeviceModelCatalog>(), provider.Resolve<IItechPowerSupplyProfileStore>(), provider.Resolve<IPowerSupplyDeviceService>(), provider.Resolve<IHiokiPowerAnalyzerDeviceService>()));
        containerRegistry.RegisterSingleton<IChannelRuntimeCoordinator>(provider =>
        {
            var backend = new CompositeDeviceIoBackend(provider.Resolve<IPowerSupplyDeviceService>(), provider.Resolve<IHiokiPowerAnalyzerDeviceService>());
            var runtime = new SimulatedChannelRuntimeCoordinator(provider.Resolve<ISequenceChannelProvider>(), backend);
            if (provider.Resolve<IManualPluginViewRegistry>() is ManualPluginViewRegistry registry)
                registry.IsBusy = () => runtime.IsAutomationActive;
            if (provider.Resolve<IDeviceModelCatalog>() is TestStation.SimulatedPlugins.SimulatedDeviceModelCatalog models)
                models.IsBusy = () => runtime.IsAutomationActive;
            return runtime;
        });
        containerRegistry.RegisterSingleton<SimulatedPluginRegistration>();
        containerRegistry.RegisterSingleton<ProtocolDialogService>();
        containerRegistry.RegisterSingleton<ISequenceExecutionHost, OpenTapHostClient>();
        containerRegistry.RegisterSingleton<ISequenceRunPersistence, SequenceRunPersistence>();
        containerRegistry.RegisterSingleton<ISequenceReportService, SequenceReportService>();
        containerRegistry.Register<IAppDialogService>(provider => provider.Resolve<ProtocolDialogService>());
        containerRegistry.Register<ISequenceDialogService>(provider => provider.Resolve<ProtocolDialogService>());
        containerRegistry.Register<ISequenceDropTargetService, SequenceDropTargetService>();
        containerRegistry.Register<ISequenceInteractionService, SequenceInteractionService>();
        containerRegistry.Register<MainWindow>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
