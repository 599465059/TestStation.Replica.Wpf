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
        control.DataSource = backend;
        control.CommandSink = backend;
        control.Width = double.NaN;
        control.Height = double.NaN;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Stretch;
        control.DeleteRequested += (_, _) => deleteRequested();
        ConfigureMoveTargets(control, widget);
        control.ConfigurationChanged += (_, _) => ConfigureMoveTargets(control, widget);
        // The library gauges use fixed internal drawing sizes. Fit those
        // drawing surfaces to canvas bounds without scaling text or inputs.
        if (control is GaugeControl or ProgressRingControl)
            control.SizeChanged += (_, _) => FitDrawingSurface(control);
        return control;
    }

    private static void ConfigureMoveTargets(InstrumentControl control, ManualWidgetViewModel widget)
    {
        if (!widget.IsInput || widget.HasCornerMoveHandle) return;
        foreach (var label in Descendants<TextBlock>(control).Where(label => label.Text == control.Settings.Name ||
                     (control is NumericFieldControl && label.Text == control.DataSource?.Channels.FirstOrDefault(channel => channel.Id == control.Channel)?.Name)))
        {
            label.Tag = "MoveHandle";
            label.Cursor = Cursors.SizeAll;
            label.ToolTip = "拖动名称移动控件";
            if (control is NumericFieldControl && LogicalTreeHelper.GetParent(label) is Border surface)
            {
                surface.Tag = "MoveHandle";
                surface.Cursor = Cursors.SizeAll;
            }
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

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
