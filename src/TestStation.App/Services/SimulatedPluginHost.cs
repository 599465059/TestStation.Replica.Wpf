using TestStation.Infrastructure.Devices.ItechPowerSupply;
using System.Text.Json;
using System.Windows;
using TestStation.Application;
using TestStation.SimulatedPlugins;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Application.Devices;

namespace TestStation.App.Services;

/// <summary>Application adapter for the sample family plugins.</summary>
public sealed class SimulatedPluginChannelProvider : ISequenceChannelProvider, ISequenceChannelChangeSource
{
    private const string ItechId = "itech.power-supply";
    private readonly IReadOnlyList<ISimulatedDevicePlugin> _plugins = SimulatedPluginCatalog.Create();
    private readonly IManualPluginViewRegistry? _registry;
    private readonly IDeviceModelCatalog _models;
    private readonly IItechPowerSupplyProfileStore _profiles;
    private readonly IPowerSupplyDeviceService? _devices;
    private readonly IHiokiPowerAnalyzerDeviceService? _hioki;
    private readonly IInstrumentDeviceService? _reference;
    public SimulatedPluginChannelProvider(IManualPluginViewRegistry? registry = null, IDeviceModelCatalog? models = null)
    { _registry = registry; _models = models ?? new SimulatedDeviceModelCatalog(); _profiles = new ItechPowerSupplyProfileStore(); }
    public SimulatedPluginChannelProvider(IManualPluginViewRegistry? registry, IDeviceModelCatalog? models, IItechPowerSupplyProfileStore profiles, IPowerSupplyDeviceService? devices = null, IHiokiPowerAnalyzerDeviceService? hioki = null, IInstrumentDeviceService? reference = null)
    {
        _devices = devices; _hioki = hioki; _reference = reference; _registry = registry; _models = models ?? new SimulatedDeviceModelCatalog(); _profiles = profiles;
        if (_devices is not null && _registry is not null)
        {
            _devices.FamilyEnabled = _registry.IsEnabled(ItechId);
            _registry.Changed += (_, _) => _devices.FamilyEnabled = _registry.IsEnabled(ItechId);
        }
    }
    private IEnumerable<ISimulatedDevicePlugin> EnabledPlugins => _plugins.Where(p => _registry is null || _registry.IsEnabled(p.Id));
    public IReadOnlyList<SequenceChannelDefinition> InstalledChannels => _plugins.Where(p => p.Id != "simulated.itech.power-supply").SelectMany(p => p.GetChannels()).Concat(_devices?.GetChannels() ?? []).Concat(_hioki?.GetChannels() ?? []).Concat(_reference?.GetChannels() ?? []).ToArray();
    public event EventHandler? Changed
    {
        add { if (_registry is not null) _registry.Changed += value; _models.Changed += value; _profiles.Changed += value; if (_devices is not null) _devices.Changed += value; if (_hioki is not null) _hioki.Changed += value; if (_reference is not null) _reference.Changed += value; }
        remove { if (_registry is not null) _registry.Changed -= value; _models.Changed -= value; _profiles.Changed -= value; if (_devices is not null) _devices.Changed -= value; if (_hioki is not null) _hioki.Changed -= value; if (_reference is not null) _reference.Changed -= value; }
    }

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => EnabledPlugins.SelectMany(plugin =>
        plugin.GetChannels().Where(c => _models.GetSelected(plugin.Id).Supports(c.Id)))
        .Concat(_devices?.GetChannels() ?? []).Concat(_hioki?.GetChannels() ?? []).Concat(_reference?.GetChannels() ?? []).ToArray();

    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() =>
        EnabledPlugins.SelectMany(plugin => plugin.DefaultValues.Where(p => _models.GetSelected(plugin.Id).Supports(p.Key)))
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
    private readonly IDeviceModelCatalog _models;
    private readonly IItechPowerSupplyProfileStore _profiles;

    public SimulatedPluginRegistration(IManualPluginViewRegistry registry, IDeviceModelCatalog models, IItechPowerSupplyProfileStore? profiles = null)
    { _registry = registry; _models = models; _profiles = profiles ?? new ItechPowerSupplyProfileStore(); }

    public void RegisterAll()
    {
        foreach (var plugin in _plugins.Where(p => p.Id != "simulated.itech.power-supply"))
            _registry.Register(new ManualProvider(plugin, _models, _profiles));
    }

    private sealed class ManualProvider : IManualPluginViewProvider
    {
        private readonly ISimulatedDevicePlugin _plugin;
        private readonly IDeviceModelCatalog _models;
        private readonly IItechPowerSupplyProfileStore _profiles;
        public ManualProvider(ISimulatedDevicePlugin plugin, IDeviceModelCatalog models, IItechPowerSupplyProfileStore profiles) { _plugin = plugin; _models = models; _profiles = profiles; }
        public string Id => _plugin.Id;
        public string Name => _plugin.Name;
        public string Version => _plugin.Version;
        public FrameworkElement CreateView(ManualPluginViewContext context)
        {
            return _plugin.CreateManualView(context.WorkspaceId, context.WorkspaceName, context.Runtime, _models);
        }
    }
}
