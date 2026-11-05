using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;
using AppThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.App.ViewModels;

public sealed record SettingsSection(string Id, string Name, string Icon);
public sealed record StopWaitChoice(int Seconds, string Text);

/// <summary>
/// Settings page. Every setting here is wired to real behavior; changes are saved immediately to
/// <see cref="IStationSettings.Location"/>. Theme changes go through <see cref="IThemeService"/>.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IStationSettings _settings;
    private readonly IThemeService _theme;
    private readonly ShellNavigationService _shell;
    private bool _loading;

    public ObservableCollection<SettingsSection> Sections { get; } =
    [
        new("look", UiText.Get("SettingsLook"), "BrushVariant"),
        new("safety", UiText.Get("SettingsSafety"), "ShieldCheckOutline"),
        new("exec", UiText.Get("SettingsExecutor"), "PlayCircleOutline"),
        new("log", UiText.Get("SettingsLogs"), "BugOutline"),
        new("about", UiText.Get("SettingsAbout"), "InformationOutline"),
    ];
    public IReadOnlyList<StopWaitChoice> StopWaitChoices { get; } =
        StationSettings.StopWaitChoices.Select(s => new StopWaitChoice(s, UiText.Format("SettingsSeconds", s))).ToArray();
    public IReadOnlyList<string> NavigationChoices { get; } = [UiText.Get("SettingsNavExpanded"), UiText.Get("SettingsNavCollapsed")];

    [ObservableProperty] private SettingsSection? _selectedSection;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _accent = "Blue";
    [ObservableProperty] private int _navigationIndex;
    [ObservableProperty] private bool _turnOutputOff;
    [ObservableProperty] private StopWaitChoice? _stopWait;
    [ObservableProperty] private string _savedText = "";

    public string SettingsFile => _settings.Location;
    public string LogDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "logs");
    public string ExecutorOverride => Environment.GetEnvironmentVariable("TESTSTATION_OPENTAP_HOST") is { Length: > 0 } path
        ? UiText.Format("SettingsExecutorOverride", path) : UiText.Get("SettingsExecutorDefault");
    public string VersionText { get; } = UiText.Format("SettingsVersion",
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "-",
        Environment.Version.ToString());

    public SettingsViewModel(IStationSettings settings, IThemeService theme, ShellNavigationService shell)
    {
        _settings = settings; _theme = theme; _shell = shell;
        SelectedSection = Sections[0];
        Load();
        _settings.Changed += SettingsChanged;
        _theme.Changed += ThemeChanged;
    }

    private void Load()
    {
        _loading = true;
        try
        {
            var s = _settings.Current;
            IsDark = _theme.CurrentMode == AppThemeMode.Dark;
            Accent = _theme.CurrentAccent.ToString();
            NavigationIndex = s.NavigationExpanded ? 0 : 1;
            TurnOutputOff = s.TurnOutputOffBeforeDisconnect;
            StopWait = StopWaitChoices.FirstOrDefault(c => c.Seconds == s.StopWaitSeconds) ?? StopWaitChoices[1];
        }
        finally { _loading = false; }
    }

    private void SettingsChanged(object? sender, EventArgs e) => Load();
    private void ThemeChanged(object? sender, EventArgs e) => Load();

    partial void OnIsDarkChanged(bool value) { if (!_loading) _theme.Apply(value ? AppThemeMode.Dark : AppThemeMode.Light, _theme.CurrentAccent); }
    partial void OnNavigationIndexChanged(int value) => Save(s => s with { NavigationExpanded = value == 0 });
    partial void OnTurnOutputOffChanged(bool value) => Save(s => s with { TurnOutputOffBeforeDisconnect = value });
    partial void OnStopWaitChanged(StopWaitChoice? value) { if (value is not null) Save(s => s with { StopWaitSeconds = value.Seconds }); }

    private void Save(Func<StationSettings, StationSettings> change)
    {
        if (_loading) return;
        try { _settings.Update(change); SavedText = UiText.Get("SettingsSaved"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SavedText = UiText.Format("SettingsSaveFailed", error.Message); }
    }

    [RelayCommand] private void UseDark() => IsDark = true;
    [RelayCommand] private void UseLight() => IsDark = false;

    [RelayCommand]
    private void UseAccent(string? accent)
    {
        if (Enum.TryParse<AccentPalette>(accent, out var value)) _theme.Apply(_theme.CurrentMode, value);
    }

    [RelayCommand]
    private void OpenLogDirectory()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo(LogDirectory) { UseShellExecute = true });
    }

    [RelayCommand] private void OpenControlPreview() => _shell.Open("控件预览");

    public void Dispose()
    {
        _settings.Changed -= SettingsChanged;
        _theme.Changed -= ThemeChanged;
    }
}
