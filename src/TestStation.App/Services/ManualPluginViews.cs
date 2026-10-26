using System.Collections.ObjectModel;
using System.Windows;

namespace TestStation.App.Services;

/// <summary>Context supplied when a plugin view is created inside one manual-test subpage.</summary>
public sealed record ManualPluginViewContext(string WorkspaceId, string WorkspaceName);

/// <summary>Metadata and factory for one device plugin's manual-test view.</summary>
public interface IManualPluginViewProvider
{
    string Id { get; }
    string Name { get; }
    string Version { get; }
    FrameworkElement CreateView(ManualPluginViewContext context);
}

public interface IManualPluginViewRegistry
{
    IReadOnlyList<IManualPluginViewProvider> Providers { get; }
    event EventHandler? Changed;
    void Register(IManualPluginViewProvider provider);
    bool Unregister(string id);
}

/// <summary>
/// Application-side registry used by the manual page host. Plugin loading can
/// populate this registry later without making MainWindow know plugin types.
/// </summary>
public sealed class ManualPluginViewRegistry : IManualPluginViewRegistry
{
    private readonly Dictionary<string, IManualPluginViewProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<IManualPluginViewProvider> Providers => new ReadOnlyCollection<IManualPluginViewProvider>(_providers.Values.ToList());
    public event EventHandler? Changed;

    public void Register(IManualPluginViewProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.Id)) throw new ArgumentException("插件界面 ID 不能为空", nameof(provider));
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new ArgumentException("插件界面名称不能为空", nameof(provider));
        _providers[provider.Id] = provider;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        var removed = _providers.Remove(id);
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }
}
