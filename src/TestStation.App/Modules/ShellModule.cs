using TestStation.Infrastructure.Devices.ItechPowerSupply;
using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.ViewModels;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.App.Views;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Infrastructure.Devices.Reference;

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
        // Status bar summary; reads IResourceCatalogService / IDeviceManagementService from ResourceModule.
        containerRegistry.RegisterSingleton<StationStatusViewModel>();
        containerRegistry.RegisterSingleton<ShellNavigationService>();
        containerRegistry.RegisterSingleton<WorkbenchViewModel>();
        containerRegistry.RegisterSingleton<WorkbenchView>();
        containerRegistry.RegisterSingleton<IStationSettings, TestStation.Infrastructure.StationSettingsStore>();
        containerRegistry.RegisterSingleton<SettingsViewModel>();
        containerRegistry.RegisterSingleton<SettingsView>();

        containerRegistry.RegisterSingleton<IDeviceModelCatalog, TestStation.SimulatedPlugins.SimulatedDeviceModelCatalog>();
        containerRegistry.RegisterSingleton<IManualPluginViewRegistry>(provider =>
        {
            var registry = new ManualPluginViewRegistry(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "disabled-plugins.json"));
            new SimulatedPluginRegistration(registry, provider.Resolve<IDeviceModelCatalog>(), provider.Resolve<IItechPowerSupplyProfileStore>()).RegisterAll();
            registry.Register(provider.Resolve<TestStation.App.Devices.ItechPowerSupply.PowerSupplyPanelProvider>());
            registry.Register(provider.Resolve<TestStation.App.Devices.HiokiPowerAnalyzer.HiokiPowerAnalyzerPanelProvider>());
            var reference = provider.Resolve<CompositeInstrumentDeviceService>();
            var referenceResources = provider.Resolve<TestStation.Application.Hardware.IResourceManager>();
            var referenceDialogs = provider.Resolve<ISequenceDialogService>();
            foreach (var family in reference.Families)
            {
                registry.Register(new TestStation.App.Devices.Reference.ReferenceScpiPanelProvider(reference, referenceResources, referenceDialogs, family.Id));
                reference.SetFamilyEnabledAsync(family.Id, registry.IsEnabled(family.Id)).GetAwaiter().GetResult();
            }
            registry.BeforeEnabledChangedAsync = (id, enabled) => reference.Families.Any(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ? reference.SetFamilyEnabledAsync(id, enabled) : Task.CompletedTask;
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
            provider.Resolve<IManualPluginViewRegistry>(), provider.Resolve<IDeviceModelCatalog>(), provider.Resolve<IItechPowerSupplyProfileStore>(), provider.Resolve<IPowerSupplyDeviceService>(), provider.Resolve<IHiokiPowerAnalyzerDeviceService>(), provider.Resolve<CompositeInstrumentDeviceService>()));
        containerRegistry.RegisterSingleton<IChannelRuntimeCoordinator>(provider =>
        {
            var backend = new CompositeDeviceIoBackend(provider.Resolve<IPowerSupplyDeviceService>(), provider.Resolve<IHiokiPowerAnalyzerDeviceService>(), provider.Resolve<CompositeInstrumentDeviceService>());
            var runtime = new SimulatedChannelRuntimeCoordinator(provider.Resolve<ISequenceChannelProvider>(), backend);
            if (provider.Resolve<IManualPluginViewRegistry>() is ManualPluginViewRegistry registry)
            {
                registry.IsBusy = () => runtime.IsAutomationActive;
                registry.ExecuteChangeAsync = operation => runtime.ExecuteManualAsync(operation);
            }
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
