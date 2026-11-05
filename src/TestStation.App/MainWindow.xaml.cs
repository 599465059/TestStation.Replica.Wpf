using TestStation.App.Localization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MaterialDesignThemes.Wpf;
using Industrial.Controls;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application;
using AppThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly IThemeService _themeService;
    private readonly ControlPreviewView _controlPreview;
    private readonly IAppDialogService _dialogs;
    private readonly TestStation.Application.Devices.ItechPowerSupply.IPowerSupplyDeviceService? _powerSupplies;
    private readonly ManualTestView _manualTest;
    private readonly DeviceTestingView? _deviceTesting;
    private readonly CanTestView? _canTest;
    private readonly RunHistoryView? _runHistory;
    private readonly ISequenceInteractionService _sequenceInteractions;
    private SequenceWorkspace? _sequenceView;
    private SequenceWorkspace SequenceView
    {
        get
        {
            if (_sequenceView is not null) return _sequenceView;
            _sequenceView = new SequenceWorkspace { Name = "SequenceView", DataContext = _viewModel };
            _sequenceView.Configure(_sequenceInteractions);
            RegisterName("SequenceView", _sequenceView);
            SequenceHost.Content = _sequenceView;
            return _sequenceView;
        }
    }
    private readonly DeviceManagementView? _deviceManagement;
    private readonly TestStation.Application.Devices.IDeviceManagementService? _managedDevices;
    private readonly string _recentFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TestStationReplica", "recent-projects.json");
    private const double ExpandedNavigationWidth = 228;
    // One-level navigation: 12px chevron column + 40px icon button + 8px margins fit in 64px.
    private const double CompactNavigationWidth = 64;
    private bool _updatingNavigationMetrics;
    private bool _closeAllowed;
    private bool _leaving;
    private bool _syncingNavigation;
    private readonly StationStatusViewModel? _stationStatus;
    private readonly WorkbenchView? _workbench;
    private readonly SettingsView? _settingsView;
    private const string SettingsPage = "settings";

    public MainWindow(MainViewModel viewModel, IThemeService themeService, ISequenceInteractionService interactions, ControlPreviewView controlPreview, IAppDialogService dialogs, ManualTestView manualTest, Views.ResourceManagementView? resourceManagement = null, CanTestView? canTest = null, RunHistoryView? runHistory = null, PluginManagementView? pluginManagement = null, DeviceTestingView? deviceTesting = null, TestStation.Application.Devices.ItechPowerSupply.IPowerSupplyDeviceService? powerSupplies = null, DeviceManagementView? deviceManagement = null, TestStation.Application.Devices.IDeviceManagementService? managedDevices = null, DeviceNavigationService? deviceNavigation = null, StationStatusViewModel? stationStatus = null,
        WorkbenchView? workbench = null, ShellNavigationService? shellNavigation = null,
        SettingsView? settingsView = null, IStationSettings? settings = null)
    {
        _settingsView = settingsView;
        if (settings is not null && powerSupplies is not null)
        {
            // Settings page → device policy (turn outputs off before disconnecting).
            powerSupplies.TurnOutputOffBeforeDisconnect = settings.Current.TurnOutputOffBeforeDisconnect;
            settings.Changed += (_, _) => powerSupplies.TurnOutputOffBeforeDisconnect = settings.Current.TurnOutputOffBeforeDisconnect;
        }
        _viewModel = viewModel; _powerSupplies = powerSupplies; _stationStatus = stationStatus; _workbench = workbench;
        if (shellNavigation is not null) shellNavigation.Requested += (_, page) => ShowPage(page);
        _themeService = themeService;
        _controlPreview = controlPreview;
        _dialogs = dialogs;
        _manualTest = manualTest;
        _deviceTesting = deviceTesting;
        _canTest = canTest;
        _runHistory = runHistory;
        _sequenceInteractions = interactions;
        _deviceManagement = deviceManagement; _managedDevices = managedDevices;
        if (deviceNavigation is not null) deviceNavigation.Requested += async (_, request) =>
        {
            if (await ShowPageAsync("device:" + request.FamilyId)) _deviceTesting?.SelectDevice(request.DeviceId);
        };
        _themeService.Apply(_themeService.CurrentMode, _themeService.CurrentAccent);
        InitializeComponent();
        StationStatusPanel.DataContext = _stationStatus;
        LiveOutputsItem.DataContext = _stationStatus;
        // The workbench replaces the legacy overview content; without it (e.g. Smoke) the old content stays.
        if (_workbench is not null) { WorkbenchHost.Content = _workbench; LegacyOverview.Visibility = Visibility.Collapsed; }
        StationStatusPanel.Visibility = _stationStatus is null ? Visibility.Collapsed : Visibility.Visible;
        BuildNavigation();
        if (_settingsView is not null) SettingsHost.Content = _settingsView;
        if (settings is { Current.NavigationExpanded: false }) NavigationItems.IsExpanded = false;
        NavigationItems.ItemInvoked += Navigation_ItemInvoked;
        NavigationItems.SizeChanged += Navigation_SizeChanged;
        NavigationItems.Loaded += (_, _) => Dispatcher.BeginInvoke(HideNavigationChrome);
        StateChanged += Window_StateChanged;
        Loaded += (_, _) => ApplyMaximizedWorkArea();
        _dialogs.Attach(AppDialogHost);
        UpdateAccentSelection();
        DataContext = _viewModel;
        _viewModel.IsDarkTheme = _themeService.CurrentMode == AppThemeMode.Dark;
        _themeService.Changed += OnThemeChanged;
        Closed += (_, _) => { _themeService.Changed -= OnThemeChanged; _deviceTesting?.Dispose(); _manualTest.Dispose(); _stationStatus?.Dispose(); _workbench?.Model.Dispose(); _settingsView?.Model.Dispose(); };
        Closing += OnWindowClosing;
        ManualTestHost.Content = _manualTest;
        PluginManagementHost.Content = pluginManagement;
        DeviceTestingHost.Content = deviceTesting;
        _viewModel.RegisterProjectSaveParticipant(_manualTest.Model);
        if (_manualTest.Panels is not null) _viewModel.RegisterProjectSaveParticipant(_manualTest.Panels);
        if (_canTest is not null) _viewModel.RegisterProjectSaveParticipant(_canTest.Model);

        PreviewKeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.S || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
            if (_viewModel.SaveProjectCommand.CanExecute(null)) _viewModel.SaveProjectCommand.Execute(null);
            args.Handled = true;
        };
        LoadRecent();
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length == 3 && arguments[1] == "--open")
            Loaded += async (_, _) => await OpenProject(arguments[2]);
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = UiText.SelectProjectParent };
        if (folder.ShowDialog(this) != true) return;
        var projectName = await _dialogs.TextAsync(this, UiText.NewProject, UiText.DefaultProjectName);
        if (string.IsNullOrWhiteSpace(projectName)) return;
        try
        {
            if (!await PrepareWorkspaceChangeAsync()) return;
            _viewModel.CreateProject(folder.FolderName, projectName);
            EnterWorkspace();
        }
        catch (Exception error) { await _dialogs.ShowInfoAsync(this, UiText.CannotCreateProject, error.Message); }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var folder = new OpenFolderDialog { Title = UiText.SelectProjectFolder };
        if (folder.ShowDialog(this) == true) await OpenProject(folder.FolderName);
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is not RecentProjectEntry recent) { ProjectPreviewCard.Visibility = Visibility.Collapsed; return; }
        var families = _deviceTesting?.Families.ToDictionary(f => f.Id, f => f.Name, StringComparer.OrdinalIgnoreCase);
        ProjectPreviewCard.DataContext = ProjectPreview.Load(recent.Path, id => families?.GetValueOrDefault(id) ?? id);
        ProjectPreviewCard.Visibility = Visibility.Visible;
    }

    private async void PreviewOpen_Click(object sender, RoutedEventArgs e)
    {
        if (RecentList.SelectedItem is RecentProjectEntry recent) await OpenProject(recent.Path);
    }

    private async void RecentList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentList.SelectedItem is RecentProjectEntry recent) await OpenProject(recent.Path);
    }

    private async Task OpenProject(string path)
    {
        try
        {
            if (!await PrepareWorkspaceChangeAsync()) return;
            _viewModel.OpenProject(path); EnterWorkspace();
        }
        catch (Exception error) { await _dialogs.ShowInfoAsync(this, UiText.CannotOpenProject, error.Message); }
    }

    private async Task<bool> PrepareWorkspaceChangeAsync()
    {
        if (_leaving) return false;
        _leaving = true;
        try
        {
            if (_deviceTesting is not null && !await _deviceTesting.PrepareToLeaveAsync()) return false;
            var manualReady = _manualTest.Model.Flush();
            if (!manualReady || !await _viewModel.PrepareToLeaveAsync()) return false;
            if (_deviceManagement is not null) await _deviceManagement.Model.LeaveAsync();
            try
            {
                // Device services switch outputs off before closing each session.
                if (_managedDevices is not null) await _managedDevices.DisconnectAllAsync();
                else if (_powerSupplies is not null) await _powerSupplies.DisconnectAsync();
            }
            catch (Exception error) when (error is IOException or AggregateException)
            {
                // Sessions are closed either way; make sure the operator knows an output may still be on.
                await _dialogs.ShowInfoAsync(this, UiText.SafeOffFailedTitle, UiText.Format("SafeOffFailedMessage", error.Message));
            }
            return true;
        }
        finally { _leaving = false; }
    }

    private async void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs args)
    {
        if (_closeAllowed) return;
        args.Cancel = true;
        if (_leaving) return;
        try
        {
            if (!await PrepareWorkspaceChangeAsync()) return;
            _closeAllowed = true;
            // Close after this Closing event has unwound, including synchronous decisions.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception error) { _viewModel.Status = error.Message; }
    }

    private void EnterWorkspace()
    {
        _manualTest.Model.OpenProject(_viewModel.ProjectPath!);
        _deviceManagement?.Model.OpenProject(_viewModel.ProjectPath!);
        _runHistory?.OpenProject(_viewModel.ProjectPath!);
        StartupView.Visibility = Visibility.Collapsed;
        WorkspaceView.Visibility = Visibility.Visible;
        SaveRecent(_viewModel.ProjectPath!);
        // Open on the workbench when it is available: check the station first, then start testing.
        ShowPage(_workbench is not null ? "工程概览" : "自动测试");
    }

    // Navigation page ids. Existing ids stay unchanged because Smoke and the
    // View menu use them; "devices" and "hardware" are new top-level pages.
    private const string DevicesPage = "devices";
    private const string HardwarePage = "hardware";

    /// <summary>
    /// One level only, so every icon in the compact rail switches a page directly.
    /// Device families and hardware tabs are chosen inside their pages; plugin
    /// load/unload therefore never rebuilds this list.
    /// </summary>
    private void BuildNavigation()
    {
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.Workbench, Icon = "ViewDashboardOutline", Tag = "工程概览" });
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.AutomaticTest, Icon = "PlayCircleOutline", Tag = "自动测试" });
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.ManualTest, Icon = "TuneVariant", Tag = "手动测试" });
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.Devices, Icon = "Devices", Tag = DevicesPage });
        if (_runHistory is not null)
            NavigationItems.Items.Add(new NavigationItem { Title = UiText.RunHistory, Icon = "ClipboardTextClockOutline", Tag = "运行记录" });
        // Configuration pages last. FooterItems cannot show a selected state (CONTROL_LIBRARY_GAPS C14).
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.HardwareConfig, Icon = "LanConnect", Tag = HardwarePage });
        NavigationItems.Items.Add(new NavigationItem { Title = UiText.PluginManagement, Icon = "PuzzleOutline", Tag = "插件管理" });
        if (_settingsView is not null)
            NavigationItems.Items.Add(new NavigationItem { Title = UiText.Settings, Icon = "CogOutline", Tag = SettingsPage });
    }

    /// <summary>Maps a page id (including legacy sub-page ids) to the navigation entry that owns it.</summary>
    private static string NavigationTagFor(string page) => page switch
    {
        _ when page.StartsWith("device:", StringComparison.Ordinal) => DevicesPage,
        "hardware:resources" or "hardware:devices" or "设备管理" or "设备配置" => HardwarePage,
        "hardware:families" => "插件管理",
        _ => page
    };

    private void LiveOutputs_Click(object sender, RoutedEventArgs e) => ShowPage(DevicesPage);

    private void ProjectSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectSwitchButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ProjectSwitchButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Navigation_ItemInvoked(object? sender, NavigationItem item)
    {
        // SelectByTag below selects tree items programmatically; ignore the echo.
        if (!_syncingNavigation && item.Tag is string page) ShowPage(page);
    }

    private void NavigateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string page }) ShowPage(page);
    }

    private async void ShowPage(string page) { try { await ShowPageAsync(page); } catch (Exception error) { _viewModel.Status = error.Message; } }

    private async Task<bool> ShowPageAsync(string page)
    {
        if (_deviceTesting is not null && DeviceTestingHost.Visibility == Visibility.Visible && !await _deviceTesting.PrepareToLeaveAsync()) return false;
        // Settings and the control preview do not need a project.
        if (!_viewModel.IsProjectOpen && page is not ("控件预览" or SettingsPage))
        {
            _viewModel.Status = "请先打开或创建工程";
            if (page == "工程概览")
            {
                ControlPreviewHost.Content = null;
                StartupView.Visibility = Visibility.Visible;
                WorkspaceView.Visibility = Visibility.Collapsed;
            }
            return false;
        }

        if (_deviceManagement is not null && ResourceHost.Visibility == Visibility.Visible) await _deviceManagement.Model.LeaveAsync();
        StartupView.Visibility = Visibility.Collapsed;
        WorkspaceView.Visibility = Visibility.Visible;
        var deviceFamily = page.StartsWith("device:", StringComparison.Ordinal) ? page[7..] : null;
        var devicesPage = deviceFamily is not null || page == DevicesPage;
        _viewModel.CurrentPage = page;
        OverviewView.Visibility = page == "工程概览" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "工程概览") _workbench?.Model.Refresh(_viewModel.ProjectName, _viewModel.ProjectPath!, _viewModel.SequenceSubprojects);
        if (page == "自动测试") _ = SequenceView;
        SequenceHost.Visibility = page == "自动测试" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "手动测试")
        {
            _manualTest.Model.OpenProject(_viewModel.ProjectPath!);
        }
        if (page == "CAN测试" && _canTest is not null) _canTest.OpenProject(_viewModel.ProjectPath!);
        if (page == "运行记录" && _runHistory is not null) { _runHistory.OpenProject(_viewModel.ProjectPath!); RunHistoryHost.Content = _runHistory; }
        ManualTestHost.Visibility = page == "手动测试" ? Visibility.Visible : Visibility.Collapsed;
        PluginManagementHost.Visibility = page is "hardware:families" or "插件管理" ? Visibility.Visible : Visibility.Collapsed;
        DeviceTestingHost.Visibility = devicesPage ? Visibility.Visible : Visibility.Collapsed;
        if (deviceFamily is not null) _deviceTesting?.ShowFamily(deviceFamily);
        else if (page == DevicesPage) _deviceTesting?.ShowDefault();
        CanTestHost.Content = page == "CAN测试" ? _canTest : null;
        CanTestHost.Visibility = page == "CAN测试" && _canTest is not null ? Visibility.Visible : Visibility.Collapsed;
        RunHistoryHost.Visibility = page == "运行记录" && _runHistory is not null ? Visibility.Visible : Visibility.Collapsed;
        var managementPage = page is HardwarePage or "hardware:resources" or "hardware:devices" or "设备管理" or "设备配置";
        // Plain "hardware" keeps the tab the user chose last time.
        if (_deviceManagement is not null && page != HardwarePage && managementPage)
            _deviceManagement.Model.ShowResources = page is "hardware:resources" or "设备配置";
        ResourceHost.Content = managementPage ? _deviceManagement : null;
        ResourceHost.Visibility = managementPage && _deviceManagement is not null ? Visibility.Visible : Visibility.Collapsed;
        ProtocolHost.Content = null;
        ProtocolHost.Visibility = Visibility.Collapsed;
        PlaceholderView.Visibility = !devicesPage && !managementPage && page != "hardware:families" && page != SettingsPage && page is not ("工程概览" or "自动测试" or "控件预览" or "手动测试" or "设备管理" or "插件管理" or "CAN测试" or "设备配置" or "运行记录") ? Visibility.Visible : Visibility.Collapsed;
        // Detach the preview on navigation so library popups and subscriptions
        // receive Unloaded. Its view model retains the sample values.
        ControlPreviewHost.Content = page == "控件预览" ? _controlPreview : null;
        ControlPreviewHost.Visibility = page == "控件预览" ? Visibility.Visible : Visibility.Collapsed;
        SettingsHost.Visibility = page == SettingsPage && _settingsView is not null ? Visibility.Visible : Visibility.Collapsed;
        _syncingNavigation = true;
        try { NavigationItems.SelectByTag(NavigationTagFor(page)); }
        finally { _syncingNavigation = false; }
        return true;
    }

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.IsProjectOpen && WorkspaceView.Visibility != Visibility.Visible) return;
        NavigationItems.IsExpanded = !NavigationItems.IsExpanded;
        ApplyNavigationMetrics();
    }

    private void Navigation_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyNavigationMetrics();
        HideNavigationChrome();
    }

    private void ApplyNavigationMetrics()
    {
        if (_updatingNavigationMetrics || NavigationItems is null) return;
        _updatingNavigationMetrics = true;
        try
        {
            var width = NavigationItems.IsExpanded ? ExpandedNavigationWidth : CompactNavigationWidth;
            if (Math.Abs(NavigationItems.Width - width) > 0.1)
                NavigationItems.Width = width;
            if (Math.Abs(NavigationColumn.Width.Value - width) > 0.1)
                NavigationColumn.Width = new GridLength(width);
            if (ProjectSwitchName is not null)
            {
                // Compact rail keeps only the folder icon; the tooltip still shows the path.
                var visibility = NavigationItems.IsExpanded ? Visibility.Visible : Visibility.Collapsed;
                ProjectSwitchName.Visibility = visibility;
                ProjectSwitchChevron.Visibility = visibility;
                ProjectSwitchButton.HorizontalContentAlignment = NavigationItems.IsExpanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            }
            if (NavigationToggleIcon is not null)
            {
                NavigationToggleIcon.Kind = NavigationItems.IsExpanded ? PackIconKind.MenuOpen : PackIconKind.Menu;
                NavigationToggleButton.ToolTip = NavigationItems.IsExpanded ? UiText.CollapseNavigation : UiText.ToggleNavigation;
            }
        }
        finally { _updatingNavigationMetrics = false; }
    }

    private void HideNavigationChrome()
    {
        foreach (var button in FindVisualChildren<Button>(NavigationItems))
        {
            // The host toolbar already exposes the navigation toggle. Hide only
            // the NavigationView's duplicate compact button; keep its item tree.
            // NavigationView creates this button in code with a MaterialDesign
            // PackIcon (Menu), so checking only for the old text glyph ("☰")
            // leaves the duplicate visible with the current Industrial.Controls.
            // Keep the check scoped to NavigationItems so toolbar buttons are
            // never affected.
            if (button.Content is PackIcon { Kind: PackIconKind.Menu }
                || button.Content is string content && content == "☰")
                button.Visibility = Visibility.Collapsed;
        }
        foreach (var textBox in FindVisualChildren<TextBox>(NavigationItems))
        {
            if (textBox.ToolTip?.ToString()?.Contains("搜索", StringComparison.Ordinal) == true)
                textBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_StateChanged(object? sender, EventArgs e) => ApplyMaximizedWorkArea();

    private void ApplyMaximizedWorkArea()
    {
        if (WindowState == WindowState.Maximized)
        {
            var workArea = SystemParameters.WorkArea;
            MaxWidth = workArea.Width;
            MaxHeight = workArea.Height;
            ShellFrame.Margin = new Thickness(8);
            ShellFrame.BorderThickness = new Thickness(1);
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
            ShellFrame.Margin = new Thickness(0);
            ShellFrame.BorderThickness = new Thickness(0);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private async void About_Click(object sender, RoutedEventArgs e) =>
        await _dialogs.ShowInfoAsync(this, UiText.About, UiText.AboutMessage);

    private void RestoreLayout_Click(object sender, RoutedEventArgs e) => SequenceView.RestoreDefaultLayout();

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        var mode = _themeService.CurrentMode == AppThemeMode.Dark ? AppThemeMode.Light : AppThemeMode.Dark;
        _themeService.Apply(mode, _themeService.CurrentAccent);
        UpdateAccentSelection();
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<AccentPalette>(tag, ignoreCase: true, out var accent)) return;
        var mode = _themeService.CurrentMode;
        _themeService.Apply(mode, accent);
        UpdateAccentSelection();
    }

    private void UpdateAccentSelection()
    {
        foreach (var button in new[] { AccentBlueButton, AccentPurpleButton, AccentTealButton })
        {
            var selected = string.Equals(button.Tag?.ToString(), _themeService.CurrentAccent.ToString(), StringComparison.OrdinalIgnoreCase);
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            button.SetResourceReference(BorderBrushProperty, selected ? "TextBrush" : "BorderBrush");
        }
    }

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        _viewModel.IsDarkTheme = _themeService.CurrentMode == AppThemeMode.Dark;
        UpdateAccentSelection();
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinButton.SetResourceReference(ForegroundProperty, Topmost ? "AccentBrush" : "TextBrush");
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void LoadRecent()
    {
        if (!File.Exists(_recentFile)) return;
        try
        {
            foreach (var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(_recentFile)) ?? [])
                if (Directory.Exists(path) && File.Exists(Path.Combine(path, "project.json"))) RecentList.Items.Add(new RecentProjectEntry(path));
        }
        catch (JsonException) { /* A damaged user preference must not prevent startup. */ }
    }

    private void SaveRecent(string path)
    {
        var paths = new[] { path }.Concat(RecentList.Items.OfType<RecentProjectEntry>().Select(item => item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        RecentList.Items.Clear();
        foreach (var item in paths) RecentList.Items.Add(new RecentProjectEntry(item));
        Directory.CreateDirectory(Path.GetDirectoryName(_recentFile)!);
        File.WriteAllText(_recentFile, JsonSerializer.Serialize(paths));
    }
}

public sealed record RecentProjectEntry(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));
}

