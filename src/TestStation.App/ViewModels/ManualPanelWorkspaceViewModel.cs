using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TestStation.Application;
using TestStation.Application.Resources;
using TestStation.App.Localization;
using TestStation.App.Services;

namespace TestStation.App.ViewModels;

public sealed record ManualDeviceChoice(string FamilyId, string DeviceId, string Name, string FamilyName, string Model)
{
    public string Caption => $"{Name} · {DeviceId} · {FamilyName}";
}

/// <summary>Owns project layout and selection; WPF panel lifetime belongs to the host adapter.</summary>
public sealed class ManualPanelWorkspaceViewModel : ObservableObject, IProjectSaveParticipant, IDisposable
{
    private readonly IResourceCatalogService _catalog;
    private readonly IManualPanelLayoutRepository _repository;
    private readonly IManualPluginViewRegistry _registry;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private ManualPanelLayout _layout = new();
    private string? _root;
    private bool _dirty;
    private bool _loadFailed;
    private bool _loading;
    private string _status = "";
    public ObservableCollection<ManualPanelGroup> Groups { get; } = [];
    public event EventHandler? LayoutChanged;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string SaveSection => UiText.ManualTest;
    public ManualPanelGroup? ActiveGroup
    {
        get => Groups.FirstOrDefault(g => g.Id == _layout.ActiveGroupId);
        set
        {
            if (_loading || value is null || !Groups.Contains(value) || value == ActiveGroup) return;
            _layout.ActiveGroupId = value.Id;
            Changed();
        }
    }
    public IReadOnlyList<ManualDeviceChoice> Choices { get; private set; } = [];
    private void RefreshChoices()
    {
        var providers = _registry.Providers.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        Choices = _catalog.Catalog.Devices.Where(d => providers.ContainsKey(d.FamilyId))
            .Select(d => new ManualDeviceChoice(d.FamilyId, d.Id, d.Name, providers[d.FamilyId].Name, d.ModelId))
            .OrderBy(d => d.FamilyName).ThenBy(d => d.Name).ToArray();
        OnPropertyChanged(nameof(Choices));
    }
    public ManualPanelWorkspaceViewModel(IResourceCatalogService catalog, IManualPanelLayoutRepository repository, IManualPluginViewRegistry registry)
    {
        _catalog = catalog; _repository = repository; _registry = registry;
        catalog.Changing += CatalogChanging;
        catalog.Changed += CatalogChanged;
        registry.Changed += CatalogChanged;
        ReloadProject();
        RefreshChoices();
    }
    private void CatalogChanging(object? sender, ResourceCatalogChangingEventArgs args)
    {
        if (string.Equals(args.Root, _root, StringComparison.OrdinalIgnoreCase)) return;
        var result = SaveProjectChanges();
        if (!result.Success) throw new InvalidOperationException(result.Error);
    }
    private void CatalogChanged(object? sender, EventArgs args)
    {
        void Refresh() { ReloadProject(); RefreshChoices(); LayoutChanged?.Invoke(this, EventArgs.Empty); }
        if (_context is not null && SynchronizationContext.Current != _context) _context.Post(_ => Refresh(), null);
        else Refresh();
    }
    private void ReloadProject()
    {
        if (string.Equals(_root, _catalog.ProjectRoot, StringComparison.OrdinalIgnoreCase) && Groups.Count > 0) return;
        _root = _catalog.ProjectRoot;
        _loadFailed = false;
        try { _layout = _root is null ? new() : _repository.Load(_root); Status = ""; }
        catch (Exception error) { _layout = new(); _loadFailed = true; Status = error.Message; }
        if (_layout.Groups.Count == 0) _layout.Groups.Add(new() { Name = UiText.Get("ManualDefaultGroup") });
        if (!_layout.Groups.Any(g => g.Id == _layout.ActiveGroupId)) _layout.ActiveGroupId = _layout.Groups[0].Id;
        _loading = true;
        try { Groups.Clear(); foreach (var group in _layout.Groups) Groups.Add(group); }
        finally { _loading = false; }
        _dirty = false;
        OnPropertyChanged(nameof(ActiveGroup));
    }
    private void Changed()
    {
        _dirty = true;
        OnPropertyChanged(nameof(ActiveGroup));
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        SaveProjectChanges();
    }
    public ManualPanelGroup AddGroup(string name)
    {
        if (_loadFailed) throw new InvalidOperationException(Status);
        name = name.Trim();
        if (name.Length is 0 or > 100 || Groups.Count >= 100 || Groups.Any(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(UiText.Get("ManualInvalidGroup"));
        var group = new ManualPanelGroup { Name = name };
        _layout.Groups.Add(group); Groups.Add(group); _layout.ActiveGroupId = group.Id; Changed(); return group;
    }
    public void RenameGroup(string name)
    {
        if (ActiveGroup is not { } group) return;
        name = name.Trim();
        if (name.Length is 0 or > 100 || Groups.Any(g => g != group && g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(UiText.Get("ManualInvalidGroup"));
        group.Name = name;
        // Collection replacement refreshes the caption without replacing the group/view instances.
        var index = Groups.IndexOf(group); Groups[index] = group; Changed();
    }
    public void DeleteGroup()
    {
        if (ActiveGroup is not { } group) return;
        if (Groups.Count == 1) { group.Panels.Clear(); group.ActiveDeviceId = null; }
        else { _layout.Groups.Remove(group); Groups.Remove(group); _layout.ActiveGroupId = Groups[0].Id; }
        Changed();
    }
    public void AddDevices(IEnumerable<ManualDeviceChoice> devices)
    {
        if (_loadFailed) throw new InvalidOperationException(Status);
        foreach (var device in devices)
        {
            if (!Choices.Any(d => d.FamilyId == device.FamilyId && d.DeviceId == device.DeviceId)) continue;
            var group = ActiveGroup!;
            if (group.Panels.Any(p => p.DeviceId.Equals(device.DeviceId, StringComparison.OrdinalIgnoreCase) && p.FamilyId.Equals(device.FamilyId, StringComparison.OrdinalIgnoreCase))) continue;
            if (group.Panels.Count == 4)
            {
                var number = 1;
                while (Groups.Any(g => g.Name == UiText.Format("ManualGroupNumber", number))) number++;
                group = AddGroup(UiText.Format("ManualGroupNumber", number));
            }
            group.Panels.Add(new(device.FamilyId, device.DeviceId)); group.ActiveDeviceId = device.DeviceId;
        }
        Changed();
    }
    public void RemovePanel(ManualPanelGroup group, ManualPanelReference panel) { group.Panels.Remove(panel); Changed(); }
    public void SetLayout(string mode) { if (ActiveGroup is { } group && group.Layout != mode) { group.Layout = mode; Changed(); } }
    public void ActivatePanel(ManualPanelGroup group, string id)
    {
        if (group.ActiveDeviceId == id) return;
        group.ActiveDeviceId = id; _dirty = true; SaveProjectChanges();
    }
    public ProjectSaveResult SaveProjectChanges()
    {
        if (_root is null || !_dirty) return new(true);
        if (_loadFailed) return new(false, Status);
        try { _repository.Save(_root, _layout); _dirty = false; Status = ""; return new(true); }
        catch (Exception error) { Status = error.Message; return new(false, error.Message); }
    }
    public void Dispose() { _catalog.Changing -= CatalogChanging; _catalog.Changed -= CatalogChanged; _registry.Changed -= CatalogChanged; }
}
