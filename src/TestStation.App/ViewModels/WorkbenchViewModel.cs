using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Resources;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

/// <summary>One device instance on the workbench. Kind: live (output on) / online / idle / fault / missing.</summary>
public sealed record WorkbenchDeviceCard(string Id, string FamilyId, string Name, string Icon, string Detail, string StateText, string Kind);

/// <summary>One recent run. Kind: success / error / idle.</summary>
public sealed record WorkbenchRunRow(string SequenceName, string When, string ResultText, string Kind);

/// <summary>
/// Home page after a project is opened: is the station ready? Shows device
/// connection state, recent runs, a project summary and plugin status. It only
/// reads existing services; it never connects, polls or writes to devices.
/// </summary>
public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
    private const int RecentRunCount = 5;
    private readonly IResourceCatalogService _catalog;
    private readonly IDeviceManagementService _devices;
    private readonly IManualPluginViewRegistry _plugins;
    private readonly ShellNavigationService _shell;
    private readonly DeviceNavigationService _deviceNavigation;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private string? _projectRoot;

    public ObservableCollection<WorkbenchDeviceCard> Devices { get; } = [];
    public ObservableCollection<WorkbenchRunRow> RecentRuns { get; } = [];

    [ObservableProperty] private string _projectName = "";
    [ObservableProperty] private string _projectPath = "";
    [ObservableProperty] private string _mainSequences = "";
    [ObservableProperty] private int _subprojectCount;
    [ObservableProperty] private int _sequenceCount;
    [ObservableProperty] private int _resourceCount;
    [ObservableProperty] private string _pluginSummary = "";
    [ObservableProperty] private string _missingPlugins = "";

    public bool HasDevices => Devices.Count > 0;
    public bool HasRuns => RecentRuns.Count > 0;
    public bool HasMissingPlugins => MissingPlugins.Length > 0;
    public int DeviceCount => Devices.Count;

    public WorkbenchViewModel(IResourceCatalogService catalog, IDeviceManagementService devices, IManualPluginViewRegistry plugins,
        ShellNavigationService shell, DeviceNavigationService deviceNavigation)
    {
        _catalog = catalog; _devices = devices; _plugins = plugins; _shell = shell; _deviceNavigation = deviceNavigation;
        _catalog.Changed += Changed;
        _plugins.Changed += Changed;
        foreach (var family in _devices.Families) family.Changed += Changed;
    }

    /// <summary>Called by the shell when the page is shown. Project data comes from the caller so this
    /// view model does not depend on the sequence editor's view model.</summary>
    public void Refresh(string projectName, string projectRoot, IEnumerable<SequenceSubproject> subprojects)
    {
        _projectRoot = projectRoot;
        ProjectName = projectName; ProjectPath = projectRoot;
        var list = subprojects.ToArray();
        SubprojectCount = list.Length;
        SequenceCount = list.Sum(p => p.Sequences.Count);
        var mains = list.Select(p => p.Sequences.FirstOrDefault(s => s.IsMain)?.Name).OfType<string>().ToArray();
        MainSequences = mains.Length == 0 ? UiText.Get("WorkbenchNone") : string.Join("、", mains);
        LoadRuns();
        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var families = _devices.Families.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var providers = _plugins.Providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Devices.Clear();
        foreach (var device in _catalog.Catalog.Devices)
        {
            families.TryGetValue(device.FamilyId, out var family);
            var icon = providers.TryGetValue(device.FamilyId, out var provider) ? NavigationIcons.ForFamily(provider) : "Devices";
            var resource = _catalog.Catalog.Transports.FirstOrDefault(t => t.Id == device.TransportId);
            var familyName = family?.Name ?? provider?.Name ?? device.FamilyId;
            var detail = resource is null ? $"{familyName}，{UiText.HwUnbound}" : $"{familyName}，{resource.Name}";
            string kind, text;
            if (family is null || !family.Enabled) { kind = "missing"; text = UiText.Get("WorkbenchPluginNotLoaded"); }
            else
            {
                var state = family.GetState(device.Id);
                if (state.Error.Length > 0) { kind = "fault"; text = UiText.HwError; }
                else if (state.Connected && state.OutputEnabled == true) { kind = "live"; text = UiText.Get("StatusLiveOne"); }
                else if (state.Connected) { kind = "online"; text = UiText.HwConnected; }
                else { kind = "idle"; text = resource is null ? UiText.HwUnbound : UiText.HwDisconnected; }
            }
            Devices.Add(new(device.Id, device.FamilyId, device.Name, icon, detail, text, kind));
        }
        ResourceCount = _catalog.Catalog.Transports.Count;

        var enabled = _plugins.InstalledProviders.Count;
        var disabled = _plugins.Providers.Where(p => !_plugins.IsEnabled(p.Id)).Select(p => p.Name).ToArray();
        PluginSummary = disabled.Length == 0
            ? UiText.Format("WorkbenchPluginsAllLoaded", enabled)
            : UiText.Format("WorkbenchPluginsSomeDisabled", enabled, string.Join("、", disabled));
        // Families that project devices need but that are not registered at all.
        MissingPlugins = string.Join("、", _catalog.Catalog.Devices.Select(d => d.FamilyId)
            .Where(id => !providers.ContainsKey(id) && !families.ContainsKey(id)).Distinct(StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasDevices)); OnPropertyChanged(nameof(DeviceCount)); OnPropertyChanged(nameof(HasMissingPlugins));
    }

    private void LoadRuns()
    {
        RecentRuns.Clear();
        if (_projectRoot is null) return;
        try
        {
            foreach (var run in new RunResultRepository(_projectRoot).LoadSummaries().Take(RecentRunCount))
            {
                var kind = run.Status switch { "completed" => "success", "failed" => "error", _ => "idle" };
                var result = run.Status switch { "completed" => UiText.Passed, "failed" => UiText.Failed, "stopped" => UiText.Stopped, _ => run.Status };
                RecentRuns.Add(new(run.SequenceName, When(run.StartedAt, run.FinishedAt - run.StartedAt), result, kind));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // An unreadable reports folder must not block the home page; the run history page shows details.
        }
        OnPropertyChanged(nameof(HasRuns));
    }

    private static string When(DateTimeOffset started, TimeSpan duration)
    {
        var local = started.ToLocalTime();
        var day = local.Date == DateTime.Today ? UiText.Get("WorkbenchToday")
            : local.Date == DateTime.Today.AddDays(-1) ? UiText.Get("WorkbenchYesterday")
            : local.ToString("M月d日", CultureInfo.InvariantCulture);
        var length = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        return $"{day} {local:HH:mm}，{UiText.Get("WorkbenchDuration")} {length}";
    }

    [RelayCommand]
    private void OpenDevice(WorkbenchDeviceCard? card)
    {
        if (card is null) return;
        if (card.Kind == "missing") _shell.Open("插件管理");
        else _deviceNavigation.Open(card.FamilyId, card.Id);
    }

    [RelayCommand] private void OpenAutomaticTest() => _shell.Open("自动测试");
    [RelayCommand] private void OpenManualTest() => _shell.Open("手动测试");
    [RelayCommand] private void OpenHardware() => _shell.Open("hardware");
    [RelayCommand] private void OpenPlugins() => _shell.Open("插件管理");
    [RelayCommand] private void OpenRunHistory() => _shell.Open("运行记录");

    // Catalog, plugin and family events can arrive from device I/O threads.
    private void Changed(object? sender, EventArgs e)
    {
        if (_context is null || SynchronizationContext.Current == _context) RefreshDevices();
        else _context.Post(_ => RefreshDevices(), null);
    }

    public void Dispose()
    {
        _catalog.Changed -= Changed;
        _plugins.Changed -= Changed;
        foreach (var family in _devices.Families) family.Changed -= Changed;
    }
}
