using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Industrial.Controls;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using AppThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class ControlsPreviewRegression
{
    public static void Verify(MainWindow window, ThemeService theme, MainViewModel sequenceModel)
    {
        var resources = System.Windows.Application.Current.Resources;
        var shellButtonStyle = resources[typeof(Button)];
        var document = sequenceModel.SelectedSequence;
        var sequenceCount = sequenceModel.Sequences.Count;
        var dictionaryCount = resources.MergedDictionaries.Count;
        NavigationSmokeHelper.Invoke(window, "控件预览"); Pump();
        var host = (ContentControl)window.FindName("ControlPreviewHost");
        var preview = (ControlPreviewView)host.Content;
        var model = (ControlPreviewViewModel)preview.DataContext;
        Require(preview.IsLoaded && preview.IsVisible && Window.GetWindow(preview) == window, "Preview is not embedded in the shell");
        var previewScroll = (ScrollViewer)preview.FindName("PreviewScroll");
        Require(previewScroll.ComputedHorizontalScrollBarVisibility != Visibility.Visible, "Preview cards do not wrap to the viewport width");
        Require(typeof(Dial).Assembly.GetName().Version?.ToString() == "1.0.0.0", "Unexpected packaged controls version");
        Require(ReferenceEquals(shellButtonStyle, resources[typeof(Button)]), "Library styles replaced the shell's global styles");
        var libraryButton = (Style)preview.FindResource("KitButton");
        var apply = (Button)preview.FindName("ApplyButton");
        Require(ReferenceEquals(apply.Style.BasedOn, libraryButton), "Preview button does not use the packaged library style");

        var dial = (Dial)preview.FindName("ValueDial");
        var gauge = (RadialGauge)preview.FindName("ValueGauge");
        var ring = (ProgressRing)preview.FindName("ValueRing");
        dial.SetCurrentValue(GaugeControl.ValueProperty, 43d);
        Pump();
        Require(model.InputValue == 43 && gauge.Value == 43 && ring.Value == 43, "Knob binding did not update the view model and instruments");
        var input = (TextBox)preview.FindName("ChannelInput");
        var checkbox = Find<CheckBox>(preview).Single(box => AutomationProperties.GetAutomationId(box) == "PreviewEnabled");
        var toggle = (IToggleProvider)new CheckBoxAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle);
        toggle.Toggle(); Pump();
        Require(!model.IsControlsEnabled && !input.IsEnabled, "Preview disabled state is not bound");
        toggle.Toggle(); Pump();
        Require(input.IsEnabled && input.ActualHeight >= 35, "Preview cannot be enabled again or compresses inputs");
        var range = (Industrial.Controls.RangeSlider)preview.FindName("RangeInput");
        range.SetCurrentValue(Industrial.Controls.RangeSlider.LowerValueProperty, 25d);
        Pump();
        Require(model.LowerValue == 25 && model.UpperValue == 80, "Range slider two-way binding failed");
        Invoke(apply);
        Require(model.Status.Contains("43.0"), "Preview command was not invoked");

        var grid = (DataGrid)preview.FindName("ChannelGrid");
        Require(grid.CanUserResizeColumns && grid.Items.Count == 3, "Preview grid lacks sample data or adjustable columns");
        grid.SelectedItem = model.Channels[0];
        var tree = (TreeView)preview.FindName("DeviceTree");
        ((TreeViewItem)((TreeViewItem)tree.Items[0]).Items[0]).IsSelected = true;
        var pageBrush = resources["PageBrush"];
        var combo = (ComboBox)preview.FindName("ModeInput");
        foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
        {
            foreach (var accent in new[] { AccentPalette.Blue, AccentPalette.Purple, AccentPalette.Teal })
            {
                theme.Apply(mode, accent); Pump();
                Require(ReferenceEquals(pageBrush, resources["PageBrush"]), "Library palette brush identity changed");
                Require(ColorOf(resources["PageBrush"]) == ColorOf(resources["WindowBrush"]), "Library background changed the application's palette");
                Require(ColorOf(resources["StrokeBrush"]) == ColorOf(resources["BorderBrush"]), "Library border palette is out of sync");
                Require(ColorOf(grid.Background) == ColorOf(resources["SurfaceBrush"]), "Preview grid background did not follow the theme");
                Require(ColorOf(input.Foreground) == ColorOf(resources["TextBrush"]), "Preview input text did not follow the theme");
                Require(IndustrialControlsThemeAdapter.ContrastRatio(ColorOf(resources["AccentTextBrush"]), ColorOf(resources["SelectionBrush"])) >= 4.5,
                    "Selected accent text lacks contrast");
                Require(IndustrialControlsThemeAdapter.ContrastRatio(ColorOf(resources["AccentForegroundBrush"]), ColorOf(resources["AccentBrush"])) >= 4.5,
                    "Primary button text lacks contrast");
                Require(!dial.AnimationEnabled && !ring.AnimationEnabled, "Preview has unexpected continuous animation");
                combo.IsDropDownOpen = true; Pump();
                var selectedItem = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(combo.SelectedIndex);
                Require(ColorOf(selectedItem.Foreground) == ColorOf(resources["SelectionTextBrush"]), "Popup list text has a stale theme");
                combo.IsDropDownOpen = false; Pump();
            }
            Capture(window, mode == AppThemeMode.Light ? "light" : "dark");
        }

        var contextButton = (Button)preview.FindName("ContextButton");
        contextButton.ContextMenu.IsOpen = true; Pump();
        theme.Apply(AppThemeMode.Light, AccentPalette.Blue); Pump();
        Require(ColorOf(contextButton.ContextMenu.Background) == ColorOf(resources["MenuBackgroundBrush"]), "Open context menu did not update its background");
        contextButton.ContextMenu.IsOpen = false;

        var colorButton = (ColorPickerButton)preview.FindName("ColorInput");
        colorButton.BringIntoView(); Pump();
        Invoke(colorButton);
        var picker = FindPopupPicker();
        Require(picker is not null, "Library color picker popup did not open");
        Require(picker!.TrySetHex("#45A1C2"), "Color picker rejected a valid HEX color");
        Pump();
        Require(model.PreviewColor == (Color)ColorConverter.ConvertFromString("#45A1C2"), "Color picker two-way binding failed");
        theme.Apply(AppThemeMode.Dark, AccentPalette.Purple); Pump();
        var hex = Find<TextBox>(picker).Single(box => box.Width == 190);
        Require(ColorOf(hex.Background) == ColorOf(resources["SurfaceBrush"]) && ColorOf(hex.Foreground) == ColorOf(resources["TextBrush"]),
            "Open color picker input retained its previous theme");
        NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
        Require(!preview.IsLoaded && FindPopupPicker() is null, "Navigating away did not unload the preview and close its popup");
        Require(ReferenceEquals(document, sequenceModel.SelectedSequence) && sequenceModel.Sequences.Count == sequenceCount,
            "Preview navigation changed the sequence document");
        Require(resources.MergedDictionaries.Count == dictionaryCount && ReferenceEquals(shellButtonStyle, resources[typeof(Button)]),
            "Repeated theme changes injected global library dictionaries");
        NavigationSmokeHelper.Invoke(window, "控件预览"); Pump();
        Require(ReferenceEquals(host.Content, preview) && model.InputValue == 43, "Preview samples were lost on navigation");
        Invoke(Find<Button>(window).Single(button => AutomationProperties.GetAutomationId(button) == "PreviewReset"));
        Require(model.InputValue == 65 && !model.IsLoggingEnabled, "Reset sample command failed");
        NavigationSmokeHelper.Invoke(window, "自动测试"); Pump();
        VerifyWithoutProject(theme);
        Console.WriteLine("PASS: packaged Industrial.Controls, six theme/accent combinations, bindings, popup theme/lifecycle, isolated styles and project-free preview navigation");
    }

    private static void VerifyWithoutProject(ThemeService theme)
    {
        var model = new MainViewModel(new AutoTestDisplayService(), new TestStation.Application.SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        var window = new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), ProtocolUiRegression.Create().Pages, new DiscardProtocolDialog(), ManualCanvasRegression.CreateView());
        try
        {
            window.Show(); Pump();
            var viewMenu = Find<Menu>(window).Single().Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "视图");
            var previewMenu = viewMenu.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "OpenControlPreview");
            previewMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
            Require(!model.IsProjectOpen && ((ContentControl)window.FindName("ControlPreviewHost")).IsVisible,
                "Preview unexpectedly requires a project");
            NavigationSmokeHelper.Invoke(window, "工程概览"); Pump();
            Require(((FrameworkElement)window.FindName("StartupView")).IsVisible, "Cannot return to the project selection page");
        }
        finally { window.Close(); Pump(); }
    }

    private static ColorPicker? FindPopupPicker()
    {
        foreach (var source in PresentationSource.CurrentSources.OfType<HwndSource>())
            if (source.RootVisual is { } root && Find<ColorPicker>(root).FirstOrDefault() is { } picker) return picker;
        return null;
    }

    private static void Invoke(Button button)
    {
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        Pump();
    }

    private static Color ColorOf(object value) => ((SolidColorBrush)value).Color;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static void Capture(Window window, string mode)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui");
        Directory.CreateDirectory(folder);
        using var output = File.Create(Path.Combine(folder, $"controls-preview-{mode}.png"));
        encoder.Save(output);
    }
}

