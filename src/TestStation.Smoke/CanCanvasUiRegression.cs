using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application.Can;
using TestStation.Application;
using TestStation.Domain.Can;
using TestStation.Infrastructure.Protocols;

namespace TestStation.Smoke;

/// <summary>Native WPF mouse regression for the CAN canvas interaction layer.</summary>
internal static class CanCanvasUiRegression
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Verify()
    {
        using var simulation = new SimulatedCanService();
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        var theme = new ThemeService();
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        model.CreateProject(Path.Combine(Path.GetTempPath(), "test-station-can-ui", Guid.NewGuid().ToString("N")), "can-ui");
        var canView = new CanTestView(new CanTestViewModel(simulation, new DbcService()));
        var protocols = ProtocolUiRegression.Create();
        var window = new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), protocols.Pages, new DiscardProtocolDialog(),
            ManualCanvasRegression.CreateView(), canTest: canView);
        try
        {
            window.Show(); window.Activate(); Pump();
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            NavigationSmokeHelper.Invoke(window, "CAN测试");
            Pump();
            var host = (ContentControl)window.FindName("CanTestHost");
            Require(host.Content is CanTestView && host.Visibility == Visibility.Visible, "CAN page was not embedded in the main window");
            var view = (CanTestView)host.Content;
            var items = (ItemsControl)view.FindName("CanvasWidgets");
            view.UpdateLayout();
            var widget = view.Model.Widgets.Single(item => item.Kind == "signal_monitor");
            var presenter = items.ItemContainerGenerator.ContainerFromItem(widget) as ContentPresenter
                ?? throw new InvalidOperationException("CAN widget presenter was not generated");

            var initial = Bounds(widget);
            var moveHandle = Find<Border>(presenter).Single(border => border.Tag?.ToString() == "CanWidgetMoveHandle");
            NativePointer.Drag(Center(moveHandle), Center(moveHandle) + new Vector(48, 32));
            Pump();
            var moved = Bounds(widget);
            Require(moved.Left > initial.Left && moved.Top > initial.Top, $"CAN header drag did not move widget: {initial} -> {moved}");

            presenter = items.ItemContainerGenerator.ContainerFromItem(widget) as ContentPresenter
                ?? throw new InvalidOperationException("CAN widget presenter disappeared before south resize");
            var south = Find<Border>(presenter).Single(border => border.Tag?.ToString() == "CanResizeS");
            var beforeSouth = Bounds(widget);
            var southPoint = Center(south);
            NativePointer.Drag(southPoint, southPoint + new Vector(0, 44));
            Pump();
            view.UpdateLayout();
            var afterSouth = Bounds(widget);
            Require(afterSouth.Height > beforeSouth.Height && afterSouth.Width == beforeSouth.Width &&
                afterSouth.Left == beforeSouth.Left && afterSouth.Top == beforeSouth.Top,
                $"CAN south resize changed the wrong dimensions: {beforeSouth} -> {afterSouth}");

            presenter = items.ItemContainerGenerator.ContainerFromItem(widget) as ContentPresenter
                ?? throw new InvalidOperationException("CAN widget presenter disappeared before corner resize");
            var southEast = Find<Border>(presenter).Single(border => border.Tag?.ToString() == "CanResizeSE");
            view.UpdateLayout();
            var cornerPoint = Center(southEast);
            var beforeCorner = Bounds(widget);
            NativePointer.Drag(cornerPoint, cornerPoint + new Vector(36, 28));
            Pump();
            var afterCorner = Bounds(widget);
            Require(afterCorner.Width > beforeCorner.Width && afterCorner.Height > beforeCorner.Height,
                $"CAN south-east resize did not change both dimensions: {beforeCorner} -> {afterCorner}");

            view.Model.SaveProjectChanges();
            using var reopened = new CanTestViewModel(new SimulatedCanService(), new DbcService());
            reopened.OpenProject(model.ProjectPath!);
            var restored = reopened.Widgets.Single(item => item.Kind == "signal_monitor");
            Require(Bounds(restored) == Bounds(widget), "CAN mouse edits were not persisted");
            Console.WriteLine("PASS: native CAN header drag, south-edge resize, south-east resize and persistence");
        }
        finally
        {
            window.Close(); Pump();
            canView.Dispose();
            app.Shutdown();
        }
    }

    private readonly record struct WidgetBounds(int Left, int Top, int Width, int Height);
    private static WidgetBounds Bounds(CanCanvasWidget item) => new(item.Left, item.Top, item.Width, item.Height);
    private static Point Center(FrameworkElement element) => element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var item in Find<T>(child)) yield return item;
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
