using System.Windows;
using System.IO;
using System.Text.Json;
using TestStation.Application;

namespace TestStation.App.Services;

/// <summary>Context supplied when a plugin view is created inside one manual-test subpage.</summary>
public sealed record ManualPluginViewContext(string WorkspaceId, string WorkspaceName, IChannelRuntimeCoordinator Runtime, IResourceEditorService? ResourceEditor = null, string? DeviceId = null);

public interface IDevicePanelSelection
{
    void SelectDevice(string deviceId);
}

/// <summary>Metadata and factory for one device plugin's manual-test view.</summary>
public interface IManualPluginViewProvider
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    FrameworkElement CreateView(ManualPluginViewContext context);
}

/// <summary>
/// Optional: a device family provider can name its MaterialDesign <c>PackIconKind</c>
/// for the side navigation. Providers without it fall back to a generic device icon.
/// </summary>
public interface INavigationIconSource
{
    string NavigationIcon { get; }
}

public interface IManualPluginViewRegistry
{
    IReadOnlyList<IManualPluginViewProvider> Providers { get; }
    IReadOnlyList<IManualPluginViewProvider> InstalledProviders { get; }
    event EventHandler? Changed;
    void Register(IManualPluginViewProvider provider);
    bool Unregister(string id);
    bool IsEnabled(string id);
    void SetEnabled(string id, bool enabled);
    Task SetEnabledAsync(string id, bool enabled) { SetEnabled(id, enabled); return Task.CompletedTask; }
}

/// <summary>
/// Application-side registry used by the manual page host. Plugin loading can
/// populate this registry later without making MainWindow know plugin types.
/// </summary>
public sealed class ManualPluginViewRegistry : IManualPluginViewRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IManualPluginViewProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _disabled;
    private readonly string? _settingsPath;
    public Func<bool>? IsBusy { get; set; }
    public Func<string, bool, Task>? BeforeEnabledChangedAsync { get; set; }
    public Func<Func<Task>, Task>? ExecuteChangeAsync { get; set; }
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    public ManualPluginViewRegistry(string? settingsPath = null)
    {
        _settingsPath = settingsPath;
        _disabled = new(StringComparer.OrdinalIgnoreCase);
        if (settingsPath is not null && File.Exists(settingsPath))
            foreach (var id in JsonSerializer.Deserialize<string[]>(File.ReadAllText(settingsPath)) ?? []) _disabled.Add(id);
        if (_disabled.Remove("simulated.itech.power-supply")) _disabled.Add("itech.power-supply");
    }
    public IReadOnlyList<IManualPluginViewProvider> Providers
    {
        get { lock (_sync) return _providers.Values.Where(p => !_disabled.Contains(p.Id)).ToArray(); }
    }
    public IReadOnlyList<IManualPluginViewProvider> InstalledProviders
    {
        get { lock (_sync) return _providers.Values.ToArray(); }
    }
    public event EventHandler? Changed;

    public void Register(IManualPluginViewProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new ArgumentException("插件界面 ID 不能为空", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new ArgumentException("插件界面名称不能为空", nameof(provider));
        lock (_sync) _providers[provider.Id] = provider;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        bool removed;
        lock (_sync)
        {
            removed = _providers.Remove(id);
            _disabled.Remove(id);
        }
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public bool IsEnabled(string id)
    {
        lock (_sync) return _providers.ContainsKey(id) && !_disabled.Contains(id);
    }

    public void SetEnabled(string id, bool enabled) => SetEnabledAsync(id, enabled).GetAwaiter().GetResult();

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await _changeGate.WaitAsync();
        try
        {
            if (ExecuteChangeAsync is not null) await ExecuteChangeAsync(() => ApplyEnabledAsync(id, enabled));
            else await ApplyEnabledAsync(id, enabled);
        }
        finally { _changeGate.Release(); }
    }

    private async Task ApplyEnabledAsync(string id, bool enabled)
    {
        bool previous;
        lock (_sync)
        {
            if (!_providers.ContainsKey(id) || enabled == !_disabled.Contains(id)) return;
            previous = !_disabled.Contains(id);
        }
        if (IsBusy?.Invoke() == true) throw new InvalidOperationException("自动测试运行期间不能变更设备状态");
        if (BeforeEnabledChangedAsync is not null) await BeforeEnabledChangedAsync(id, enabled);
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                await dispatcher.InvokeAsync(() => SetEnabledCore(id, enabled));
            else SetEnabledCore(id, enabled);
        }
        catch (Exception error)
        {
            try { if (BeforeEnabledChangedAsync is not null) await BeforeEnabledChangedAsync(id, previous); }
            catch (Exception cleanup) { error.Data["FamilyStateRollback"] = cleanup.ToString(); }
            throw;
        }
    }

    private void SetEnabledCore(string id, bool enabled)
    {
        if (IsBusy?.Invoke() == true) throw new InvalidOperationException("自动测试运行期间不能变更设备状态");
        lock (_sync)
        {
            if (!_providers.ContainsKey(id) || enabled == !_disabled.Contains(id)) return;
            var previous = _disabled.ToArray();
            if (enabled) _disabled.Remove(id); else _disabled.Add(id);
            try
            {
                if (_settingsPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                    File.WriteAllText(_settingsPath + ".tmp", JsonSerializer.Serialize(_disabled));
                    File.Move(_settingsPath + ".tmp", _settingsPath, true);
                }
            }
            catch { _disabled.Clear(); _disabled.UnionWith(previous); throw; }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
