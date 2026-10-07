using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Industrial.Controls.Instruments;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Domain;

namespace TestStation.Smoke;

internal static class IndustrialCanvasRegression
{
    public static void Verify(ManualTestView view, Window window)
    {
        Require(typeof(InstrumentControl).Assembly.GetName().Version == new Version(1, 0, 0, 0), "Canvas did not load the 1.0.0 library");
        var catalog = InstrumentCatalog.Default();
        Require(catalog.Items.Select(item => item.Key).Order().SequenceEqual(ManualWidgetCatalog.Items.Select(item => item.LibraryKey).Order()),
            "Canvas palette does not cover every published industrial instrument");
        var scroll = (ScrollViewer)view.FindName("CanvasScroll");
        scroll.ScrollToHorizontalOffset(0); scroll.ScrollToVerticalOffset(0); Pump();
        var items = (ItemsControl)view.FindName("CanvasItems");
        var backend = new MemoryBackend();
        var adapter = new ManualWidgetConfigurationAdapter();
        var factory = new ManualInstrumentFactory(adapter);
        foreach (var spec in ManualWidgetCatalog.Items)
        {
            view.Model.AddWidgetAt(spec.Kind, 2, 2); Pump(); window.UpdateLayout();
            var widget = view.Model.Widgets.Last();
            var presenter = (ContentPresenter)items.ItemContainerGenerator.ContainerFromItem(widget);
            var instrument = Find<InstrumentControl>(presenter).Single();
            using var expected = catalog.Create(spec.LibraryKey);
            Require(instrument.GetType() == expected.GetType(), $"{spec.Kind} fell back to the wrong control");
            Require(Math.Abs(presenter.ActualWidth - widget.Width) < .1 && Math.Abs(presenter.ActualHeight - widget.Height) < .1,
                $"{spec.Kind} ignored canvas dimensions");
            var before = widget.Bounds;
            Point drag;
            if (spec.Interactive)
            {
                var grip = Find<FrameworkElement>(presenter).First(element => element.Tag is "MoveHandle" && element.IsVisible);
                drag = grip.PointToScreen(new Point(Math.Min(30, grip.ActualWidth / 2), grip.ActualHeight / 2));
                Require(grip.Cursor == Cursors.SizeAll, $"{spec.Kind} move grip has the wrong cursor");
            }
            else drag = presenter.PointToScreen(new Point(90, Math.Min(24, presenter.ActualHeight / 2)));
            NativePointer.Drag(drag, drag + new Vector(32, 16)); Pump();
            Require(widget.Bounds == before with { Column = before.Column + 2, Row = before.Row + 1 }, $"{spec.Kind} native move did not snap");
            var resize = Find<Border>(presenter).Single(element => element.Tag is "SouthEast");
            var resizeStart = resize.PointToScreen(new Point(4, 4));
            NativePointer.Drag(resizeStart, resizeStart + new Vector(16, 16)); Pump();
            Require(widget.Bounds.Columns == before.Columns + 1 && widget.Bounds.Rows == before.Rows + 1,
                $"{spec.Kind} resize did not follow the grid");
            if (instrument is NumericFieldControl)
            {
                window.UpdateLayout();
                var row = Find<Grid>(instrument).FirstOrDefault(item => item.ColumnDefinitions.Count == 3
                    && item.ColumnDefinitions[1].Width.Value is >= 4 and <= 6);
                Require(row is not null && row.ActualHeight >= instrument.ActualHeight - 2,
                    $"{spec.Kind} numeric row did not stretch vertically: row={row?.ActualHeight}, control={instrument.ActualHeight}");
            }

            // Complex library-specific state must survive a project save,
            // not just the common name/minimum/maximum settings.
            if (instrument is OscilloscopeControl scope)
            { scope.SetVisibleChannels(["CH2", "CH4"]); scope.ApplyViewState(1, 3, -2, 2, 1.5, 2.5); }
            if (instrument is RangeSliderControl range) range.UpperValue = 65;
            if (instrument is SerialDataDisplay serial) { serial.SetDisplayMode(SerialDisplayMode.Hex); serial.ShowTime = false; }
            if (instrument is NumericFieldControl numeric) numeric.NameColumnWidth = 70;
            Require(view.Model.SaveDocument(), $"{spec.Kind} project save failed");
            using var restored = factory.Create(new ManualWidgetViewModel(widget.Model, view.Model.GridSize), backend, () => { });
            Require(restored.GetType() == instrument.GetType(), $"{spec.Kind} restored another type");
            if (restored is OscilloscopeControl loadedScope)
                Require(loadedScope.VisibleChannels.SequenceEqual(["CH2", "CH4"]) && loadedScope.ViewXMin == 1 && loadedScope.FirstCursorX == 1.5, "Scope channels/view/cursors were lost");
            if (restored is RangeSliderControl loadedRange) Require(loadedRange.UpperValue == 65, "Range upper limit was lost");
            if (restored is SerialDataDisplay loadedSerial) Require(loadedSerial.DisplayMode == SerialDisplayMode.Hex && !loadedSerial.ShowTime, "Serial options were lost");
            if (restored is NumericFieldControl loadedNumeric) Require(loadedNumeric.NameColumnWidth == 70, "Numeric name divider width was lost");
            var reloaded = ManualCanvasRegression.CreateView();
            reloaded.Model.OpenProject(window.DataContext is MainViewModel main ? main.ProjectPath! : throw new InvalidOperationException("Missing test project"));
            Require(reloaded.Model.Widgets.Single(item => item.Id == widget.Id).Bounds == widget.Bounds, $"{spec.Kind} layout did not reload");
            reloaded.Dispose();
            VerifyInternalInteraction(instrument, widget, presenter);
            view.Model.DeleteWidget(widget); Pump();
            Console.WriteLine($"PASS: {spec.Kind} / {instrument.GetType().Name}: native move/resize, configuration and internal interaction");
        }
    }

    private static void VerifyInternalInteraction(InstrumentControl instrument, ManualWidgetViewModel widget, ContentPresenter presenter)
    {
        var bounds = widget.Bounds;
        if (instrument is RealtimeNumericInputControl)
        {
            var input = Find<TextBox>(presenter).First();
            NativePointer.Click(input.PointToScreen(new Point(input.ActualWidth / 2, input.ActualHeight / 2))); Pump();
            Require(input.IsKeyboardFocusWithin, "Input field was captured by canvas move");
        }
        if (instrument is NumericFieldControl)
        {
            var divider = Find<GridSplitter>(presenter).First();
            var start = divider.PointToScreen(new Point(divider.ActualWidth / 2, divider.ActualHeight / 2));
            NativePointer.Click(start); Pump();
            Require(((NumericFieldControl)instrument).NameColumnWidth >= 70 && widget.Bounds == bounds,
                $"Name divider hit zone affected canvas: width {((NumericFieldControl)instrument).NameColumnWidth}, bounds {widget.Bounds}, previous {bounds}");
        }
        if (instrument is ToggleControl or CommandButtonControl or TriggerButtonControl)
        {
            var button = Find<Button>(presenter).First();
            NativePointer.Click(button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2))); Pump();
            Require(instrument.CommandStatus.Contains("已接收"), "Command button did not reach the simulation backend");
        }
        if (instrument is SerialDataDisplay)
        {
            var option = Find<CheckBox>(presenter).First(); var wasChecked = option.IsChecked;
            NativePointer.Click(option.PointToScreen(new Point(option.ActualWidth / 2, option.ActualHeight / 2))); Pump();
            Require(option.IsChecked != wasChecked, "Serial checkbox was captured by canvas move");
        }
        Require(widget.Bounds == bounds, "Internal control interaction moved the canvas instrument");
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
