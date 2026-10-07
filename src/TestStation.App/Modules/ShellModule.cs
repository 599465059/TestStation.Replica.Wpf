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
        containerRegistry.RegisterSingleton<IThemeService, ThemeService>();
        containerRegistry.RegisterSingleton<IAutoTestDisplayService, AutoTestDisplayService>();
        containerRegistry.RegisterSingleton<ISequenceEditingService, SequenceEditingService>();
        containerRegistry.RegisterSingleton<ISequenceDocumentService, SequenceDocumentService>();
        containerRegistry.Register<ISequenceDialogService>(provider => (ISequenceDialogService)provider.Resolve<IProtocolDialogService>());
        containerRegistry.Register<ISequenceDropTargetService, SequenceDropTargetService>();
        containerRegistry.Register<ISequenceInteractionService, SequenceInteractionService>();
        containerRegistry.Register<MainWindow>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
    }
}
