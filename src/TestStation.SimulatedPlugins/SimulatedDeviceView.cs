using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

internal abstract class SimulatedDeviceView : Border
{
    protected readonly IChannelRuntimeCoordinator Runtime;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Control> _operationControls = [];
    protected readonly StackPanel Body = new();
    protected readonly TextBlock Status = new();

    protected SimulatedDeviceView(string title, string subtitle, IChannelRuntimeCoordinator runtime)
    {
        Runtime = runtime;
        Padding = new Thickness(16);
        Margin = new Thickness(8);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        var root = new StackPanel();
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var titleBlock = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.SemiBold };
        titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var subtitleBlock = new TextBlock { Text = subtitle, Margin = new Thickness(0, 4, 0, 0) };
        subtitleBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        heading.Children.Add(titleBlock); heading.Children.Add(subtitleBlock);
        root.Children.Add(heading); root.Children.Add(Body);
        Status.Margin = new Thickness(0, 14, 0, 0); Status.TextWrapping = TextWrapping.Wrap;
        Status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(Status);
        Child = root;
        Runtime.AvailabilityChanged += RuntimeAvailabilityChanged;
    }

    protected void TrackSubscription(IDisposable subscription) => _subscriptions.Add(subscription);
    protected void TrackOperation(Control control) { _operationControls.Add(control); control.IsEnabled = !Runtime.IsAutomationActive; }
    protected void OnPolled(string channelId, Action<ChannelRuntimeValue> update) =>
        TrackSubscription(Runtime.Subscribe(channelId, TimeSpan.FromMilliseconds(500), update));

    private void RuntimeAvailabilityChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var control in _operationControls) control.IsEnabled = !Runtime.IsAutomationActive;
        SetStatus(Runtime.IsAutomationActive ? "自动测试占用设备，手动轮询和操作已暂停。" : "手动轮询已恢复。", Runtime.IsAutomationActive);
    });

    protected TextBlock Label(string text) => new()
    {
        Text = text, Margin = new Thickness(0, 8, 0, 4),
        Foreground = FindBrush("MutedBrush")
    };

    protected TextBox NumberBox(string value)
    {
        var box = new TextBox { Text = value, MinWidth = 110, MinHeight = 30, Padding = new Thickness(7, 3, 7, 3) };
        box.SetResourceReference(Control.BackgroundProperty, "WindowBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        return box;
    }

    protected Button Button(string text, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        button.Click += click;
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        else button.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonStyle");
        TrackOperation(button);
        return button;
    }

    protected TextBlock Value(string initial = "—")
    {
        var block = new TextBlock { Text = initial, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        return block;
    }

    protected static bool TryNumber(TextBox input, out double value) =>
        double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    protected Brush FindBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    protected void SetStatus(string message, bool error = false)
    {
        Status.Text = message;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    protected override void OnVisualParentChanged(DependencyObject? oldParent)
    {
        base.OnVisualParentChanged(oldParent);
        if (Parent is null)
        {
            Runtime.AvailabilityChanged -= RuntimeAvailabilityChanged;
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    protected static Grid FormGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    protected static void Put(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column); grid.Children.Add(element);
    }
}
