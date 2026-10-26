using System.Text.Json;
using System.Windows;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

/// <summary>
/// Boundary exposed by a family plugin. It contains device metadata, channel
/// declarations, default simulator values and an optional manual view.
/// </summary>
public interface ISimulatedDevicePlugin : ISequenceChannelProvider
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    IReadOnlyDictionary<string, JsonElement> DefaultValues { get; }
    FrameworkElement CreateManualView(string workspaceId, string workspaceName, IChannelRuntimeCoordinator runtime);
}

public static class SimulatedPluginCatalog
{
    public static IReadOnlyList<ISimulatedDevicePlugin> Create() =>
    [new SimulatedItechPowerSupplyPlugin(), new SimulatedElectronicLoadPlugin(), new SimulatedDigitalMultimeterPlugin()];
}
