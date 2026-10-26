using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.ViewModels;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.App.Views;

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
        containerRegistry.RegisterSingleton<IManualPluginViewRegistry, ManualPluginViewRegistry>();
        containerRegistry.RegisterSingleton<IAutoTestDisplayService, AutoTestDisplayService>();
        containerRegistry.RegisterSingleton<ISequenceEditingService, SequenceEditingService>();
        containerRegistry.RegisterSingleton<ISequenceDocumentService, SequenceDocumentService>();
        containerRegistry.RegisterSingleton<IAutomaticWorkspaceService, AutomaticWorkspaceService>();
        containerRegistry.RegisterSingleton<ISequenceStepCatalog, BuiltInSequenceStepCatalog>();
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
