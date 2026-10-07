using System.Windows;
using Industrial.Controls.Instruments;
using TestStation.App.ViewModels;

namespace TestStation.App.Services;

/// <summary>Creates and hosts the published Industrial.Controls instruments used by the canvas.</summary>
public sealed class ManualInstrumentFactory(ManualWidgetConfigurationAdapter configuration)
{
    public InstrumentControl Create(ManualWidgetViewModel widget, MemoryBackend backend, Action deleteRequested)
    {
        var control = CreateControl(widget.Model.Kind);
        configuration.Apply(control, widget);
        control.DataSource = backend;
        control.CommandSink = backend;
        control.Width = double.NaN;
        control.Height = double.NaN;
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.VerticalAlignment = VerticalAlignment.Stretch;
        control.DeleteRequested += (_, _) => deleteRequested();
        return control;
    }

    private static InstrumentControl CreateControl(string kind) => kind switch
    {
        "input" => new RealtimeNumericInputControl(),
        "serial" => new SerialDataDisplay(),
        "gauge" => new GaugeControl(),
        "status" => new StatusIndicatorControl(),
        "command" => new CommandButtonControl(),
        _ => new RealtimeNumericDisplayControl()
    };
}
