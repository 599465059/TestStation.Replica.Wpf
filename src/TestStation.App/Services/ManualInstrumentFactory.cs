using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Industrial.Controls.Instruments;
using TestStation.App.ViewModels;

namespace TestStation.App.Services;

/// <summary>Creates and hosts the published Industrial.Controls instruments used by the canvas.</summary>
public sealed class ManualInstrumentFactory(ManualWidgetConfigurationAdapter configuration)
{
    public InstrumentControl Create(ManualWidgetViewModel widget, MemoryBackend backend, Action deleteRequested)
    {
        var control = ManualWidgetCatalog.Create(widget.Model.Kind);
        configuration.Apply(control, widget);
        // The fresh demonstration layout shows two independent live traces;
        // existing saved configurations keep their own channel selection.
        if (control is OscilloscopeControl && (string.IsNullOrWhiteSpace(widget.Model.ConfigurationJson) || widget.Model.ConfigurationJson == "{}"))
            ((OscilloscopeControl)control).SetVisibleChannels(["CH1", "CH2"]);
        // Command-oriented inputs submit to the transport but do not consume
        // the channel feedback stream. Otherwise a simulated/real feedback
        // frame immediately overwrites the value the operator just selected.
        control.DataSource = IsCommandInput(widget.Model.Kind) ? null : backend;
        control.CommandSink = backend;
        control.Width = double.NaN;
        control.Height = double.NaN;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Stretch;
        control.DeleteRequested += (_, _) => deleteRequested();
        ConfigureMoveTargets(control, widget);
        control.ConfigurationChanged += (_, _) => ConfigureMoveTargets(control, widget);
        // Industrial.Controls 1.0.0 keeps the shared numeric name/value row at
        // a compact fixed height. The canvas can resize the host vertically,
        // so clear that local height in the application adapter and let the
        // row stretch with its host. This keeps the library package untouched.
        if (control is NumericFieldControl)
            ExpandNumericRows(control);
        // The library gauges use fixed internal drawing sizes. Fit those
        // drawing surfaces to canvas bounds without scaling text or inputs.
        if (control is GaugeControl or ProgressRingControl)
            control.SizeChanged += (_, _) => FitDrawingSurface(control);
        return control;
    }

    private static bool IsCommandInput(string kind) => kind is
        "input" or "slider" or "range" or "toggle" or "mode" or "command" or "trigger";

    private static void ConfigureMoveTargets(InstrumentControl control, ManualWidgetViewModel widget)
    {
        if (!widget.IsInput) return;
        var labels = Descendants<TextBlock>(control)
            .Where(label => label.Text == control.Settings.Name ||
                (control is NumericFieldControl && label.Text == control.DataSource?.Channels.FirstOrDefault(channel => channel.Id == control.Channel)?.Name))
            .ToList();
        // Industrial.Controls 1.1.0 displays the channel id when the input
        // control has no feedback DataSource. Use its first visible label as
        // the name/drag region rather than making the whole input draggable.
        if (labels.Count == 0)
            labels = Descendants<TextBlock>(control).Where(label => !string.IsNullOrWhiteSpace(label.Text)).Take(1).ToList();
        foreach (var label in labels)
        {
            label.Tag = "MoveHandle";
            label.Cursor = Cursors.SizeAll;
            label.ToolTip = "拖动名称移动控件";
        }
    }

    private static void FitDrawingSurface(InstrumentControl control)
    {
        var size = Math.Max(48, Math.Min(control.ActualWidth - 12, control.ActualHeight - 32));
        if (control is GaugeControl)
            foreach (var gauge in Descendants<Industrial.Controls.RadialGauge>(control))
            { gauge.Height = size; gauge.Width = size; }
        if (control is ProgressRingControl)
            foreach (var ring in Descendants<Industrial.Controls.ProgressRing>(control))
            { ring.Height = size; ring.Width = size; }
    }

    private static void ExpandNumericRows(InstrumentControl control)
    {
        foreach (var row in Descendants<Grid>(control)
                     .Where(item => item.ColumnDefinitions.Count == 3
                                    && item.ColumnDefinitions[1].Width.Value >= 4
                                    && item.ColumnDefinitions[1].Width.Value <= 6))
        {
            row.ClearValue(FrameworkElement.HeightProperty);
            row.MinHeight = 32;
            row.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
