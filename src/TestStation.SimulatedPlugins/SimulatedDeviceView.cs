using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

internal abstract class SimulatedDeviceView : Border, IDisposable
{
    protected readonly IChannelRuntimeCoordinator Runtime;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Control> _operationControls = [];
    private readonly List<(string Channel, Action<ChannelRuntimeValue> Update)> _polling = [];
    private bool _disposed;
    private readonly IDeviceModelCatalog _models;
    private readonly string _familyId;
    private readonly ComboBox _modelSelector;
    private readonly List<(string Channel, FrameworkElement Element)> _channelElements = [];
    private bool _syncingModel;
    private bool _pollError;
    protected readonly StackPanel Body = new();
    protected readonly TextBlock Status = new();

    protected SimulatedDeviceView(IChannelRuntimeCoordinator runtime, IDeviceModelCatalog models, string familyId)
    {
        Runtime = runtime; _models = models; _familyId = familyId;
        Padding = new Thickness(12);
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Left;
        MaxWidth = 640;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(BorderBrushProperty, "BorderBrush");
        var root = new StackPanel();
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var modelLabel = new TextBlock { Text = DeviceText.Get("Model"), Width = 48, VerticalAlignment = VerticalAlignment.Center };
        modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        _modelSelector = new ComboBox { ItemsSource = models.GetModels(familyId), DisplayMemberPath = nameof(DeviceModelDefinition.Name),
            SelectedItem = models.GetSelected(familyId), MinWidth = 192 };
        System.Windows.Automation.AutomationProperties.SetName(_modelSelector, DeviceText.Get("DeviceModel"));
        TrackOperation(_modelSelector);
        _modelSelector.SelectionChanged += (_, _) =>
        {
            if (_syncingModel || _modelSelector.SelectedItem is not DeviceModelDefinition model) return;
            try { _models.Select(_familyId, model.Id); }
            catch (Exception error) { RefreshModel(); SetStatus(error.Message, true); }
        };
        modelRow.Children.Add(modelLabel); modelRow.Children.Add(_modelSelector); root.Children.Add(modelRow);
        root.Children.Add(Body);
        Status.Margin = new Thickness(0, 8, 0, 0); Status.TextWrapping = TextWrapping.Wrap;
        Status.Visibility = Visibility.Collapsed;
        Status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        root.Children.Add(Status);
        Child = root;
        Runtime.AvailabilityChanged += RuntimeAvailabilityChanged;
        _models.Changed += ModelChanged;
        Loaded += (_, _) => UpdatePolling();
        Unloaded += (_, _) => StopPolling();
        IsVisibleChanged += (_, _) => UpdatePolling();
    }

    protected void TrackSubscription(IDisposable subscription) => _subscriptions.Add(subscription);
    protected void TrackOperation(Control control) { _operationControls.Add(control); control.IsEnabled = !Runtime.IsAutomationActive; }
    protected void OnPolled(string channelId, Action<ChannelRuntimeValue> update) => _polling.Add((channelId, update));
    protected bool Supports(string channelId) => _models.GetSelected(_familyId).Supports(channelId);
    protected void ForChannel(FrameworkElement element, string channelId)
    {
        _channelElements.Add((channelId, element));
        element.Visibility = Supports(channelId) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ModelChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) RefreshModel();
        else Dispatcher.BeginInvoke(RefreshModel);
    }

    private void RefreshModel()
    {
        if (_disposed) return;
        _syncingModel = true;
        try { _modelSelector.SelectedItem = _models.GetSelected(_familyId); }
        finally { _syncingModel = false; }
        foreach (var (channel, element) in _channelElements)
            element.Visibility = Supports(channel) ? Visibility.Visible : Visibility.Collapsed;
        SetStatus("");
        UpdatePolling();
    }

    private void UpdatePolling()
    {
        if (_disposed) return;
        StopPolling();
        if (!IsLoaded || !IsVisible) return;
        foreach (var (channel, update) in _polling)
        {
            if (!Supports(channel)) continue;
            TrackSubscription(Runtime.Subscribe(channel, TimeSpan.FromMilliseconds(500), value =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_disposed || !IsVisible || !Supports(channel) || Runtime.IsAutomationActive) return;
                    if (value.Error.Length > 0) { _pollError = true; SetStatus(value.Error, true); return; }
                    if (_pollError) { _pollError = false; SetStatus(""); }
                    update(value);
                });
            }));
        }
        Body.IsEnabled = !Runtime.IsAutomationActive;
        if (Runtime.IsAutomationActive) SetStatus("自动测试中，手动操作已暂停。");
    }

    private void StopPolling()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
    }

    private void RuntimeAvailabilityChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        Body.IsEnabled = !Runtime.IsAutomationActive;
        foreach (var control in _operationControls) control.IsEnabled = !Runtime.IsAutomationActive;
        SetStatus(Runtime.IsAutomationActive ? "自动测试中，手动操作已暂停。" : "", Runtime.IsAutomationActive);
    });

    protected TextBlock Label(string text)
    {
        var label = new TextBlock { Text = text, Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeights.SemiBold };
        // Resource reference, not a looked-up brush, so the label follows theme switches.
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }

    protected TextBox NumberBox(string value)
    {
        var box = new TextBox { Text = value, MinWidth = 110, MinHeight = 28, Padding = new Thickness(7, 3, 7, 3) };
        box.SetResourceReference(Control.BackgroundProperty, "InputBackgroundBrush");
        box.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        box.SetResourceReference(Control.BorderBrushProperty, "InputBorderBrush");
        return box;
    }

    protected Button Button(string text, Func<object?, RoutedEventArgs, Task> click, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (sender, args) =>
        {
            try { await click(sender, args); }
            catch (Exception error) { SetStatus(error.Message, true); }
        };
        if (accent) button.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        else button.SetResourceReference(FrameworkElement.StyleProperty, "AppButtonStyle");
        TrackOperation(button);
        return button;
    }

    protected TextBlock Value(string initial = "—")
    {
        var block = new TextBlock { Text = initial, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Right };
        // Measured values are data, not actions: text colour, monospaced digits. Accent is reserved for interaction.
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        block.SetResourceReference(TextBlock.FontFamilyProperty, "ReadoutFontFamily");
        return block;
    }

    protected static bool TryNumber(TextBox input, out double value) =>
        double.TryParse(input.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    protected Brush FindBrush(string key) =>
        TryFindResource(key) as Brush ?? Brushes.Gray;

    protected void SetStatus(string message, bool error = false)
    {
        Status.Text = message;
        Status.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        Status.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorBrush" : "MutedBrush");
    }

    public virtual void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Runtime.AvailabilityChanged -= RuntimeAvailabilityChanged;
        _models.Changed -= ModelChanged;
        StopPolling();
    }

    protected static Grid FormGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        return grid;
    }

    protected StackPanel Section(string title)
    {
        var section = new StackPanel { Width = 240, Margin = new Thickness(0, 0, 20, 8) };
        section.Children.Add(Label(title));
        return section;
    }

    protected static void Put(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column); grid.Children.Add(element);
    }
}
