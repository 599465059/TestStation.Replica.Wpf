using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

public sealed class SimulatedDigitalMultimeterPlugin : ISimulatedDevicePlugin
{
    public string Id => "simulated.digital-multimeter";
    public string Name => "模拟数字万用表";
    public string Version => "0.1.0";

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
    [
        new("DMM01.Voltage.Measurement", "number", "数字万用表", CanRead: true, CanWrite: false),
        new("DMM01.Current.Measurement", "number", "数字万用表", CanRead: true, CanWrite: false),
        new("DMM01.Continuity", "boolean", "数字万用表", CanRead: true, CanWrite: false)
    ];

    public IReadOnlyDictionary<string, JsonElement> DefaultValues { get; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
    {
        ["DMM01.Voltage.Measurement"] = JsonSerializer.SerializeToElement(11.97),
        ["DMM01.Current.Measurement"] = JsonSerializer.SerializeToElement(0.41),
        ["DMM01.Continuity"] = JsonSerializer.SerializeToElement(true)
    };

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName, IChannelRuntimeCoordinator runtime, IDeviceModelCatalog? models = null) =>
        new DigitalMultimeterView(runtime, models ?? new SimulatedDeviceModelCatalog());
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class DigitalMultimeterView : SimulatedDeviceView
{
    private readonly TextBlock _voltage;
    private readonly TextBlock _current;
    private readonly TextBlock _continuity;

    public DigitalMultimeterView(IChannelRuntimeCoordinator runtime, IDeviceModelCatalog models) : base(runtime, models, "simulated.digital-multimeter")
    {
        var measurements = Section("测量值"); Body.Children.Add(measurements);
        var voltage = FormGrid(); Put(voltage, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltage = Value(); Put(voltage, _voltage, 1); measurements.Children.Add(voltage);
        var current = FormGrid(); Put(current, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = Value(); Put(current, _current, 1); measurements.Children.Add(current);
        ForChannel(current, "DMM01.Current.Measurement");
        var continuity = FormGrid(); Put(continuity, new TextBlock { Text = "通断", VerticalAlignment = VerticalAlignment.Center }, 0);
        _continuity = Value(); Put(continuity, _continuity, 1); measurements.Children.Add(continuity);
        ForChannel(continuity, "DMM01.Continuity");
        var read = Button("读取一次", Read, true); read.Margin = new Thickness(0, 8, 0, 0); measurements.Children.Add(read);
        OnPolled("DMM01.Voltage.Measurement", value => Dispatcher.BeginInvoke(() => _voltage.Text = $"{value.Value.GetDouble():0.###} V"));
        OnPolled("DMM01.Current.Measurement", value => Dispatcher.BeginInvoke(() => _current.Text = $"{value.Value.GetDouble():0.###} A"));
        OnPolled("DMM01.Continuity", value => Dispatcher.BeginInvoke(() => _continuity.Text = value.Value.GetBoolean() ? "导通" : "断开"));
    }

    private async Task Read(object? sender, RoutedEventArgs e)
    {
        var voltage = await Runtime.ReadOnceAsync("DMM01.Voltage.Measurement", "manual");
        _voltage.Text = $"{voltage.Value.GetDouble():0.###} V";
        if (Supports("DMM01.Current.Measurement"))
        {
            var current = await Runtime.ReadOnceAsync("DMM01.Current.Measurement", "manual");
            _current.Text = $"{current.Value.GetDouble():0.###} A";
        }
        if (Supports("DMM01.Continuity"))
        {
            var continuity = await Runtime.ReadOnceAsync("DMM01.Continuity", "manual");
            _continuity.Text = continuity.Value.GetBoolean() ? "导通" : "断开";
        }
        SetStatus("");
    }
}
