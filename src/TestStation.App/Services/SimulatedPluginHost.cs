using System.Text.Json;
using System.Windows;
using TestStation.Application;
using TestStation.SimulatedPlugins;

namespace TestStation.App.Services;

/// <summary>Application adapter for the sample family plugins.</summary>
public sealed class SimulatedPluginChannelProvider : ISequenceChannelProvider
{
    private readonly IReadOnlyList<ISimulatedDevicePlugin> _plugins = SimulatedPluginCatalog.Create();

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
        _plugins.SelectMany(plugin => plugin.GetChannels()).ToArray();

    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() =>
        _plugins.SelectMany(plugin => plugin.DefaultValues)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Converts the plugin project's WPF-neutral host contract into the
/// application manual-page contract. The plugin never references MainWindow
/// or a ViewModel.
/// </summary>
public sealed class SimulatedPluginRegistration
{
    private readonly IManualPluginViewRegistry _registry;
    private readonly IReadOnlyList<ISimulatedDevicePlugin> _plugins = SimulatedPluginCatalog.Create();

    public SimulatedPluginRegistration(IManualPluginViewRegistry registry) => _registry = registry;

    public void RegisterAll()
    {
        foreach (var plugin in _plugins)
            _registry.Register(new ManualProvider(plugin));
    }

    private sealed class ManualProvider : IManualPluginViewProvider
    {
        private readonly ISimulatedDevicePlugin _plugin;
        public ManualProvider(ISimulatedDevicePlugin plugin) => _plugin = plugin;
        public string Id => _plugin.Id;
        public string Name => _plugin.Name;
        public string Version => _plugin.Version;
        public FrameworkElement CreateView(ManualPluginViewContext context) =>
            _plugin.CreateManualView(context.WorkspaceId, context.WorkspaceName, context.Runtime);
    }
}
