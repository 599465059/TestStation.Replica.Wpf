using Prism.Ioc;
using Prism.Modularity;
using TestStation.App.Services;
using TestStation.App.ViewModels.Protocols;
using TestStation.App.Views.Protocols;
using TestStation.Application.Protocols;
using TestStation.Infrastructure.Protocols;

namespace TestStation.App.Modules;
public sealed class ProtocolModule : IModule
{
    public void RegisterTypes(IContainerRegistry registry)
    {
        registry.RegisterSingleton<IProtocolRepository, ProtocolRepository>();
        registry.RegisterSingleton<IProtocolLibraryService, ProtocolLibraryService>();
        registry.RegisterSingleton<IProtocolEditingService, ProtocolEditingService>();
        registry.RegisterSingleton<IProtocolPreviewService, ProtocolPreviewService>();
        registry.RegisterSingleton<IDbcService, DbcService>();
        registry.RegisterSingleton<IProtocolDialogService, ProtocolDialogService>();
        registry.RegisterSingleton<ProtocolLibraryViewModel>();
        registry.RegisterSingleton<ProtocolEditorViewModel>();
        registry.RegisterSingleton<DbcBrowserViewModel>();
        registry.RegisterSingleton<ProtocolTestViewModel>();
        registry.RegisterSingleton<ProtocolLibraryView>(); registry.RegisterSingleton<ProtocolEditorView>();
        registry.RegisterSingleton<DbcBrowserView>(); registry.RegisterSingleton<ProtocolTestView>();
        registry.RegisterSingleton<ProtocolPages>();
    }
    public void OnInitialized(IContainerProvider containerProvider) { }
}
