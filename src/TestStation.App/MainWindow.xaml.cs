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
    private readonly ProtocolPages _protocolPages;
    private readonly IProtocolDialogService _dialogs;
    private readonly ManualTestView _manualTest;
    private readonly ManualWorkspaceView? _manualWorkspace;
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
    private readonly Views.ResourceManagementView? _resourceManagement;
    private readonly string _recentFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TestStationReplica", "recent-projects.json");
    private const double ExpandedNavigationWidth = 228;
    private const double CompactNavigationWidth = 76;
    private bool _updatingNavigationMetrics;
    private bool _closeAllowed;
    private bool _leaving;

    public MainWindow(MainViewModel viewModel, IThemeService themeService, ISequenceInteractionService interactions, ControlPreviewView controlPreview, ProtocolPages protocolPages, IProtocolDialogService dialogs, ManualTestView manualTest, Views.ResourceManagementView? resourceManagement = null, ManualWorkspaceView? manualWorkspace = null)
    {
        _viewModel = viewModel;
        _themeService = themeService;
        _controlPreview = controlPreview;
        _protocolPages = protocolPages;
        _dialogs = dialogs;
        _manualTest = manualTest;
        _manualWorkspace = manualWorkspace;
        _sequenceInteractions = interactions;
        _resourceManagement = resourceManagement;
        _themeService.Apply(_themeService.CurrentMode, _themeService.CurrentAccent);
        InitializeComponent();
        BuildNavigation();
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
        Closed += (_, _) => _themeService.Changed -= OnThemeChanged;
        Closing += OnWindowClosing;
        ManualTestHost.Content = (object?)_manualWorkspace ?? _manualTest;
        _viewModel.RegisterProjectSaveParticipant(_manualWorkspace is null ? _manualTest.Model : _manualWorkspace.Model);
        _viewModel.RegisterProjectSaveParticipant(_protocolPages.SaveParticipant);
        if (_resourceManagement?.DataContext is ResourceManagementViewModel resourceModel)
            _viewModel.RegisterProjectSaveParticipant(resourceModel);
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
        if (_protocolPages?.CanChangeProject() == false) return;
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
            var manualReady = _manualWorkspace is null ? _manualTest.Model.Flush() : await _manualWorkspace.Model.PrepareToLeaveAsync();
            return manualReady && _protocolPages.CanChangeProject() && await _viewModel.PrepareToLeaveAsync();
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
        _protocolPages?.OpenProject(_viewModel.ProjectPath!);
        if (_manualWorkspace is not null) _manualWorkspace.Model.OpenProject(_viewModel.ProjectPath!);
        else _manualTest.Model.OpenProject(_viewModel.ProjectPath!);
        if (_resourceManagement?.DataContext is ViewModels.ResourceManagementViewModel resources)
            resources.OpenProject(_viewModel.ProjectPath!);
        StartupView.Visibility = Visibility.Collapsed;
        WorkspaceView.Visibility = Visibility.Visible;
        SaveRecent(_viewModel.ProjectPath!);
        ShowPage("工程概览");
    }

    private void BuildNavigation()
    {
        var testSystem = new NavigationItem { Title = "测试系统", Icon = "TestTube" };
        testSystem.Children.Add(new NavigationItem { Title = UiText.Workspace, Icon = "ViewDashboardOutline", Tag = "工程概览" });
        testSystem.Children.Add(new NavigationItem { Title = UiText.DeviceConfiguration, Icon = "Chip", Tag = "设备配置" });
        testSystem.Children.Add(new NavigationItem { Title = UiText.ManualTest, Icon = "TuneVariant", Tag = "手动测试" });
        testSystem.Children.Add(new NavigationItem { Title = UiText.AutomaticTest, Icon = "PlayCircleOutline", Tag = "自动测试" });
        testSystem.Children.Add(new NavigationItem { Title = UiText.RunHistory, Icon = "History", Tag = "运行记录" });

        var protocols = new NavigationItem { Title = UiText.ProtocolManagement, Icon = "FileCodeOutline" };
        protocols.Children.Add(new NavigationItem { Title = UiText.ProtocolLibrary, Icon = "DatabaseOutline", Tag = "协议库" });
        protocols.Children.Add(new NavigationItem { Title = UiText.ProtocolEditor, Icon = "FileCodeOutline", Tag = "自定义协议" });
        protocols.Children.Add(new NavigationItem { Title = UiText.DbcDatabase, Icon = "CodeBraces", Tag = "DBC 数据库" });
        protocols.Children.Add(new NavigationItem { Title = UiText.ProtocolTest, Icon = "TestTube", Tag = "协议测试" });

        var settings = new NavigationItem { Title = UiText.Settings, Icon = "CogOutline" };
        settings.Children.Add(new NavigationItem { Title = UiText.ControlPreview, Icon = "PaletteOutline", Tag = "控件预览" });

        NavigationItems.Items.Add(testSystem);
        NavigationItems.Items.Add(protocols);
        NavigationItems.Items.Add(settings);
    }

    private void Navigation_ItemInvoked(object? sender, NavigationItem item)
    {
        if (item.Tag is string page) ShowPage(page);
    }

    private void NavigateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string page }) ShowPage(page);
    }

    private void ShowPage(string page)
    {
        if (!_viewModel.IsProjectOpen && page != "控件预览")
        {
            _viewModel.Status = "请先打开或创建工程";
            if (page == "工程概览")
            {
                ControlPreviewHost.Content = null;
                StartupView.Visibility = Visibility.Visible;
                WorkspaceView.Visibility = Visibility.Collapsed;
            }
            return;
        }

        StartupView.Visibility = Visibility.Collapsed;
        WorkspaceView.Visibility = Visibility.Visible;
        _viewModel.CurrentPage = page;
        OverviewView.Visibility = page == "工程概览" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "自动测试") _ = SequenceView;
        SequenceHost.Visibility = page == "自动测试" ? Visibility.Visible : Visibility.Collapsed;
        if (page == "手动测试")
        {
            if (_manualWorkspace is not null) _manualWorkspace.Model.OpenProject(_viewModel.ProjectPath!);
            else _manualTest.Model.OpenProject(_viewModel.ProjectPath!);
        }
        ManualTestHost.Visibility = page == "手动测试" ? Visibility.Visible : Visibility.Collapsed;
        ResourceHost.Content = page == "设备配置" ? _resourceManagement : null;
        ResourceHost.Visibility = page == "设备配置" && _resourceManagement is not null ? Visibility.Visible : Visibility.Collapsed;
        var protocolPage = _protocolPages?.Get(page);
        ProtocolHost.Content = protocolPage;
        ProtocolHost.Visibility = protocolPage is not null ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderView.Visibility = protocolPage is null && page is not ("工程概览" or "自动测试" or "控件预览" or "手动测试" or "设备配置") ? Visibility.Visible : Visibility.Collapsed;
        // Detach the preview on navigation so library popups and subscriptions
        // receive Unloaded. Its view model retains the sample values.
        ControlPreviewHost.Content = page == "控件预览" ? _controlPreview : null;
        ControlPreviewHost.Visibility = page == "控件预览" ? Visibility.Visible : Visibility.Collapsed;
        NavigationItems.SelectByTag(page);
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
        }
        finally { _updatingNavigationMetrics = false; }
    }

    private void HideNavigationChrome()
    {
        foreach (var button in FindVisualChildren<Button>(NavigationItems))
        {
            // The host toolbar already exposes the navigation toggle. Hide only
            // the NavigationView's duplicate compact button; keep its item tree.
            if (button.Content is string content && content == "☰")
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

