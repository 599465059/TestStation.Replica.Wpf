using TestStation.Application;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.App.Localization;
using TestStation.App.ViewModels;
using TestStation.Domain.Resources;

namespace TestStation.App.Services;

public interface IResourceEditorService
{
    Task<TransportResource?> EditAsync(TransportResource? resource = null, IReadOnlyList<string>? allowedTypes = null);
}

public sealed class ResourceEditorService(IResourceConfigurationService configuration, ITransportDiscoveryService discovery,
    IChannelRuntimeCoordinator runtime, ProtocolDialogService dialogs) : IResourceEditorService
{
    public async Task<TransportResource?> EditAsync(TransportResource? resource = null, IReadOnlyList<string>? allowedTypes = null)
    {
        if (runtime.IsAutomationActive) throw new InvalidOperationException(UiText.Get("HwAutoLocked"));
        var model = new TransportResourceEditorViewModel(configuration, discovery, resource, allowedTypes);
        var view = new Views.TransportResourceEditorView { DataContext = model };
        while (await dialogs.ShowEditorAsync(UiText.Get("HwResourceEditor"), view))
        {
            try
            {
                var draft = model.BuildDraft();
                await runtime.ExecuteManualAsync(() => { configuration.Save(draft); return Task.CompletedTask; });
                return draft;
            }
            catch (Exception error) { model.Error = error.Message; }
        }
        return null;
    }
}
