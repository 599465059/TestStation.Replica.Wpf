using System.Windows;
using Prism.Ioc;
using TestStation.App.Services;
using TestStation.Application.Devices;
using TestStation.Application.Resources;
using TestStation.Application.Hardware;

namespace TestStation.App.Devices.Reference;

public sealed class ReferenceScpiPanelProvider(IInstrumentDeviceService service, IResourceManager resources, ISequenceDialogService dialogs, string familyId) : IManualPluginViewProvider, INavigationIconSource
{
    public string Id => familyId;
    public string Name => service.Families.First(f => f.Id.Equals(familyId, StringComparison.OrdinalIgnoreCase)).Name;
    public string Version => "1.0";
    public string NavigationIcon => "Chip";
    public FrameworkElement CreateView(ManualPluginViewContext context) => new ReferenceScpiView(new ReferenceScpiViewModel(familyId, service, context.Runtime, resources, dialogs, context.DeviceId));
}

public static class ReferenceScpiModuleRegistration
{
    public static void Register(IContainerRegistry registry) { }
}
