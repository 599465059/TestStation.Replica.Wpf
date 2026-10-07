using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using System.Collections.Specialized;
using MaterialDesignThemes.Wpf;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Application;
using TestStation.Domain;
using Industrial.Controls.Instruments;

namespace TestStation.App.Views;

public partial class ManualTestView : UserControl, IDisposable
{
    private bool _workspaceActive = true;
    private bool _disposed;
    public bool SamplesRunning => _samples.IsEnabled;
    private const string WidgetFormat = "TestStation.ManualWidgetKind";
    public ManualTestViewModel Model { get; }
    private readonly DispatcherTimer _samples = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DateTime _clock = DateTime.UtcNow;
    private readonly MemoryBackend _backend = new();
    private readonly Dictionary<string, InstrumentControl> _instrumentControls = new(StringComparer.Ordinal);
    private readonly ManualInstrumentFactory _instrumentFactory;
    private readonly ManualWidgetConfigurationAdapter _configuration;
    private readonly IThemeService? _theme;
    private ManualWidgetTool? _paletteTool;
    private Point _palettePress;
    private readonly ManualCanvasInteractionController _interaction;
    private bool _libraryExpanded = true;
    private bool _propertyExpanded = true;
    private string? _paletteCategory;
    private double _libraryExpandedWidth = 228;
    private double _propertyExpandedWidth = 300;
    private bool _applyingConfiguration;
    private double _scopeTime;
    public bool IsManipulating => _interaction.IsManipulating;
    public ManualTestView(ManualTestViewModel model, ManualInstrumentFactory? instrumentFactory = null, ManualWidgetConfigurationAdapter? configuration = null, IThemeService? theme = null)
    {
        Model = model;
        _configuration = configuration ?? new ManualWidgetConfigurationAdapter();
        _theme = theme;
        _instrumentFactory = instrumentFactory ?? new ManualInstrumentFactory(_configuration);
        InitializeComponent(); DataContext = model;
        _libraryExpanded = false;
        _propertyExpanded = false;
        LibraryColumn.Width = new GridLength(46);
        PropertyColumn.Width = new GridLength(46);
        LibraryContent.Visibility = Visibility.Collapsed;
        LibraryIconRail.Visibility = Visibility.Visible;
        WidgetPropertyPanel.Visibility = Visibility.Collapsed;
        LibraryTitle.Visibility = Visibility.Collapsed;
        PropertyTitle.Visibility = Visibility.Collapsed;
        LibraryToggleIcon.Kind = PackIconKind.ChevronRight;
        PropertyToggleIcon.Kind = PackIconKind.ChevronLeft;
        _interaction = new ManualCanvasInteractionController(Model, CanvasSurface, CanvasItems, SelectionMarquee);
        Model.WidgetConfigurationChanged += RefreshInstrument;
        Model.CaptureConfigurationsRequested += CaptureConfigurations;
        Model.Widgets.CollectionChanged += OnWidgetsChanged;
        _samples.Tick += (_, _) =>
        {
            var value = 12 + Math.Sin((DateTime.UtcNow - _clock).TotalSeconds) * 0.25;
            // Only numeric instruments consume the view-model sample. The
            // serial console receives its own records below; updating every
            // widget here would cause unrelated controls to invalidate while
            // another widget is being dragged.
            foreach (var widget in Model.Widgets.Where(item => item.IsDisplay))
                widget.Value = value.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);
            _backend.Publish(new NumericSample("CH1", value, DateTimeOffset.UtcNow));
            _backend.Publish(new SerialRecord(DateTimeOffset.UtcNow, "RX", System.Text.Encoding.ASCII.GetBytes($"CH1={value:F3}\\r\\n")));
            if (_instrumentControls.Values.Any(control => control is OscilloscopeControl))
            {
                var samples = Enumerable.Range(0, 100).Select(i => Math.Sin((_scopeTime + i * .001) * 2 * Math.PI * 2)).ToArray();
                _backend.Publish(new SampleBatch("CH1", samples, _scopeTime, .001));
                _scopeTime += .1;
            }
        };
        Loaded += (_, _) => { if (_theme is not null) { _theme.Changed -= OnThemeChanged; _theme.Changed += OnThemeChanged; } UpdateSampleActivity(); };
        IsVisibleChanged += (_, _) => UpdateSampleActivity();
        Unloaded += (_, _) => { if (_theme is not null) _theme.Changed -= OnThemeChanged; _interaction.Cancel(); _samples.Stop(); };
    }
    public void SetWorkspaceActive(bool active)
    {
        _workspaceActive = active;
        if (!active) _interaction.Cancel();
        UpdateSampleActivity();
    }
    private void UpdateSampleActivity()
    {
        if (!_disposed && _workspaceActive && IsLoaded && IsVisible) _samples.Start();
        else _samples.Stop();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _samples.Stop(); _interaction.Cancel();
        if (_theme is not null) _theme.Changed -= OnThemeChanged;
        Model.WidgetConfigurationChanged -= RefreshInstrument;
        Model.CaptureConfigurationsRequested -= CaptureConfigurations;
        Model.Widgets.CollectionChanged -= OnWidgetsChanged;
        foreach (var control in _instrumentControls.Values) control.Dispose();
        _instrumentControls.Clear();
    }

    private void LibraryToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_libraryExpanded && LibraryColumn.Width.Value > 46)
            _libraryExpandedWidth = LibraryColumn.Width.Value;

        _libraryExpanded = !_libraryExpanded;
        LibraryColumn.Width = new GridLength(_libraryExpanded ? Math.Max(190, _libraryExpandedWidth) : 46);
        LibraryContent.Visibility = _libraryExpanded ? Visibility.Visible : Visibility.Collapsed;
        LibraryIconRail.Visibility = _libraryExpanded ? Visibility.Collapsed : Visibility.Visible;
        LibraryTitle.Visibility = _libraryExpanded ? Visibility.Visible : Visibility.Collapsed;
        LibraryToggleIcon.Kind = _libraryExpanded ? PackIconKind.ChevronLeft : PackIconKind.ChevronRight;
        LibraryToggleButton.ToolTip = _libraryExpanded ? "收起控件库" : "展开控件库";
    }

    private void PaletteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string category }) return;
        _paletteCategory = category;
        if (Resources["WidgetToolsView"] is CollectionViewSource source)
            source.View.Filter = item => item is ManualWidgetTool tool && string.Equals(tool.Category, _paletteCategory, StringComparison.Ordinal);
        if (!_libraryExpanded) LibraryToggle_Click(LibraryToggleButton, new RoutedEventArgs(Button.ClickEvent));
    }

    private void PropertyToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_propertyExpanded && PropertyColumn.Width.Value > 46)
            _propertyExpandedWidth = PropertyColumn.Width.Value;

        _propertyExpanded = !_propertyExpanded;
        PropertyColumn.Width = new GridLength(_propertyExpanded ? Math.Max(260, _propertyExpandedWidth) : 46);
        WidgetPropertyPanel.Visibility = _propertyExpanded ? Visibility.Visible : Visibility.Collapsed;
        PropertyTitle.Visibility = _propertyExpanded ? Visibility.Visible : Visibility.Collapsed;
        PropertyToggleIcon.Kind = _propertyExpanded ? PackIconKind.ChevronRight : PackIconKind.ChevronLeft;
        PropertyToggleButton.ToolTip = _propertyExpanded ? "收起属性" : "展开属性";
    }

    private void RefreshInstrument(ManualWidgetViewModel item)
    {
        if (!_instrumentControls.TryGetValue(item.Id, out var control)) return;
        try
        {
            _applyingConfiguration = true;
            _configuration.Apply(control, item);
        }
        catch (Exception) { }
        finally { _applyingConfiguration = false; }
    }

    private void CaptureConfigurations()
    {
        foreach (var widget in Model.Widgets)
            if (_instrumentControls.TryGetValue(widget.Id, out var control))
                Model.StoreWidgetConfiguration(widget, _configuration.Capture(control), persist: false);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Redraw only on an actual theme change. Stable brush updates alone
        // cannot refresh the scope's ScottPlot colors and cached readouts.
        _applyingConfiguration = true;
        try { foreach (var control in _instrumentControls.Values) control.ApplySettings(control.ExportSettings()); }
        finally { _applyingConfiguration = false; }
    }

    private void OnWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var id in _instrumentControls.Keys.Where(id => Model.Widgets.All(widget => widget.Id != id)).ToArray())
        {
            _instrumentControls[id].Dispose();
            _instrumentControls.Remove(id);
        }
    }

    private void InstrumentHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ContentControl host || host.Content is not null || host.DataContext is not ManualWidgetViewModel item) return;
        var control = _instrumentFactory.Create(item, _backend, () => Model.DeleteWidget(item));
        _instrumentControls[item.Id] = control;
        // A widget created with an older canvas file has no settings snapshot.
        // Capture the library defaults once so future versions can extend the
        // settings object without changing the canvas schema.
        Model.StoreWidgetConfiguration(item, _configuration.Capture(control));
        control.ConfigurationChanged += (_, _) =>
        {
            if (_applyingConfiguration || _disposed) return;
            Model.StoreWidgetConfiguration(item, _configuration.Capture(control), persist: false);
            if (Model.Selected == item) Model.Select(item);
        };
        host.Content = control;
    }

    private void Palette_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _paletteTool = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ManualWidgetTool;
        _palettePress = e.GetPosition(WidgetPalette);
    }

    private void Palette_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _paletteTool is null || !Model.AddWidgetCommand.CanExecute(null)) return;
        var delta = e.GetPosition(WidgetPalette) - _palettePress;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var kind = _paletteTool.Kind;
        _paletteTool = null;
        DragDrop.DoDragDrop(WidgetPalette, new DataObject(WidgetFormat, kind), DragDropEffects.Copy);
        e.Handled = true;
    }

    private void Palette_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ManualWidgetTool tool) return;
        AddAt(tool.Kind, new Point(CanvasScroll.HorizontalOffset + 32, CanvasScroll.VerticalOffset + 32));
        _paletteTool = null;
        e.Handled = true;
    }

    private void Canvas_DragOver(object sender, DragEventArgs e)
    {
        // Other payloads can belong to an instrument's text editor or console.
        if (!e.Data.GetDataPresent(WidgetFormat)) return;
        e.Effects = Model.AddWidgetCommand.CanExecute(null) && e.Data.GetData(WidgetFormat) is string kind && Model.WidgetTools.Any(tool => tool.Kind == kind)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Canvas_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(WidgetFormat)) return;
        e.Handled = true;
        if (e.Data.GetData(WidgetFormat) is string kind && Model.WidgetTools.Any(tool => tool.Kind == kind) && Model.AddWidgetCommand.CanExecute(null))
            AddAt(kind, e.GetPosition(CanvasSurface));
    }

    private void AddAt(string kind, Point position)
    {
        Model.AddWidgetAt(kind,
            (int)Math.Round(position.X / Model.GridSize, MidpointRounding.AwayFromZero),
            (int)Math.Round(position.Y / Model.GridSize, MidpointRounding.AwayFromZero));
        CanvasSurface.Focus();
    }

    private void Canvas_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Instruments keep their packaged configuration/delete menus.
        if (Ancestor<ContentPresenter>(e.OriginalSource as DependencyObject)?.DataContext is ManualWidgetViewModel) return;
        if (!Model.IsDesignMode) { e.Handled = true; return; }
        var position = Mouse.GetPosition(CanvasSurface);
        var menu = CanvasSurface.ContextMenu;
        menu.Items.Clear();
        foreach (var category in Model.WidgetTools.GroupBy(tool => tool.Category))
        {
            var group = new MenuItem { Header = category.Key };
            foreach (var tool in category)
            {
                var item = new MenuItem
                {
                    Header = tool.Name,
                    Icon = new PackIcon { Kind = Enum.Parse<PackIconKind>(tool.Icon), Width = 17, Height = 17 },
                    IsEnabled = Model.AddWidgetCommand.CanExecute(null)
                };
                item.Click += (_, _) => AddAt(tool.Kind, position);
                group.Items.Add(item);
            }
            menu.Items.Add(group);
        }
    }

    private static DependencyObject? WalkParent(DependencyObject node) => node switch
    {
        Visual or Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        ContentElement content => ContentOperations.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node)
    };

    private static T? Ancestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null) { if (node is T target) return target; node = WalkParent(node); }
        return null;
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0)
            Model.SetViewport(CanvasScroll.ViewportWidth, CanvasScroll.ViewportHeight);
    }
    private void OnPointerDown(object sender, MouseButtonEventArgs e) => _interaction.OnPointerDown(e);
    private void OnPointerMove(object sender, MouseEventArgs e) => _interaction.OnPointerMove(e);
    private void OnPointerUp(object sender, MouseButtonEventArgs e) => _interaction.OnPointerUp(e);
    private void OnCaptureLost(object sender, MouseEventArgs e) => _interaction.OnCaptureLost();
    private void OnKeyDown(object sender, KeyEventArgs e) => _interaction.OnKeyDown(e);
}
