using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;

namespace TestStation.App.ViewModels;

public sealed record PluginManagementRow(string Id, string Name, string Version, bool IsEnabled)
{
    public string State => UiText.Get(IsEnabled ? "PluginEnabled" : "PluginDisabled");
    public string StateBrush => IsEnabled ? "SuccessBrush" : "MutedBrush";
}

public partial class PluginManagementViewModel : ObservableObject
{
    private readonly IManualPluginViewRegistry _registry;
    private readonly IChannelRuntimeCoordinator _runtime;
    public ObservableCollection<PluginManagementRow> Plugins { get; } = [];
    [ObservableProperty] private PluginManagementRow? _selectedPlugin;
    [ObservableProperty] private string _status = "";
    public bool CanEnable => !_runtime.IsAutomationActive && SelectedPlugin is { IsEnabled: false };
    public bool CanDisable => !_runtime.IsAutomationActive && SelectedPlugin is { IsEnabled: true };

    public PluginManagementViewModel(IManualPluginViewRegistry registry, IChannelRuntimeCoordinator runtime)
    {
        _registry = registry; _runtime = runtime;
        _registry.Changed += (_, _) => Refresh();
        _runtime.AvailabilityChanged += (_, _) => System.Windows.Application.Current.Dispatcher.InvokeAsync(NotifyAvailability);
        Refresh();
    }

    private void Refresh()
    {
        var selectedId = SelectedPlugin?.Id;
        Plugins.Clear();
        foreach (var plugin in _registry.InstalledProviders)
            Plugins.Add(new(plugin.Id, plugin.Name, plugin.Version, _registry.IsEnabled(plugin.Id)));
        SelectedPlugin = Plugins.FirstOrDefault(p => p.Id == selectedId) ?? Plugins.FirstOrDefault();
        NotifyAvailability();
    }

    partial void OnSelectedPluginChanged(PluginManagementRow? value) => NotifyAvailability();
    private void NotifyAvailability()
    {
        EnablePluginCommand.NotifyCanExecuteChanged(); DisablePluginCommand.NotifyCanExecuteChanged();
        if (_runtime.IsAutomationActive) Status = UiText.Get("PluginBusy");
        else if (Status == UiText.Get("PluginBusy")) Status = "";
    }

    [RelayCommand(CanExecute = nameof(CanEnable))]
    private void EnablePlugin() => SetEnabled(true);
    [RelayCommand(CanExecute = nameof(CanDisable))]
    private void DisablePlugin() => SetEnabled(false);

    private void SetEnabled(bool enabled)
    {
        if (_runtime.IsAutomationActive || SelectedPlugin is not { } plugin) return;
        try
        {
            _registry.SetEnabled(plugin.Id, enabled);
            Status = UiText.Format("PluginStateChanged", plugin.Name, UiText.Get(enabled ? "PluginEnabled" : "PluginDisabled"));
        }
        catch (Exception error) { Status = error.Message; }
    }
}
