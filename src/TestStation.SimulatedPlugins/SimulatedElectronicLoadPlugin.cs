using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

public sealed class SimulatedElectronicLoadPlugin : ISimulatedDevicePlugin
{
    public string Id => "simulated.electronic-load";
    public string Name => "模拟电子负载";
    public string Version => "0.1.0";

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
    [
        new("LOAD01.Current.Setpoint", "number", "电子负载", CanRead: true, CanWrite: true),
        new("LOAD01.Voltage.Measurement", "number", "电子负载", CanRead: true, CanWrite: false),
        new("LOAD01.Current.Measurement", "number", "电子负载", CanRead: true, CanWrite: false),
        new("LOAD01.Input.Enabled", "boolean", "电子负载", CanRead: true, CanWrite: true)
    ];

    public IReadOnlyDictionary<string, JsonElement> DefaultValues { get; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
    {
        ["LOAD01.Current.Setpoint"] = JsonSerializer.SerializeToElement(0.5),
        ["LOAD01.Voltage.Measurement"] = JsonSerializer.SerializeToElement(11.96),
        ["LOAD01.Current.Measurement"] = JsonSerializer.SerializeToElement(0.49),
        ["LOAD01.Input.Enabled"] = JsonSerializer.SerializeToElement(false)
    };

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName, IChannelRuntimeCoordinator runtime, IDeviceModelCatalog? models = null) =>
        new ElectronicLoadView(runtime, models ?? new SimulatedDeviceModelCatalog());
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class ElectronicLoadView : SimulatedDeviceView
{
    private readonly TextBox _current;
    private readonly TextBlock _voltageMeasurement;
    private readonly TextBlock _currentMeasurement;
    private readonly CheckBox _input;

    public ElectronicLoadView(IChannelRuntimeCoordinator runtime, IDeviceModelCatalog models) : base(runtime, models, "simulated.electronic-load")
    {
        var groups = new WrapPanel(); Body.Children.Add(groups);
        var settings = Section("负载设定"); groups.Children.Add(settings);
        var measurements = Section("测量值"); groups.Children.Add(measurements);
        var current = FormGrid();
        Put(current, new TextBlock { Text = "电流 (A)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = NumberBox("0.5"); Put(current, _current, 1); settings.Children.Add(current);
        _input = new CheckBox { Content = "输入开启", Margin = new Thickness(0, 6, 0, 0) };
        _input.SetResourceReference(Control.ForegroundProperty, "TextBrush"); settings.Children.Add(_input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Button("应用设定", Apply, true)); actions.Children.Add(Button("读取一次", Read)); settings.Children.Add(actions);
        var voltageReading = FormGrid();
        Put(voltageReading, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltageMeasurement = Value(); Put(voltageReading, _voltageMeasurement, 1); measurements.Children.Add(voltageReading);
        ForChannel(voltageReading, "LOAD01.Voltage.Measurement");
        var currentReading = FormGrid();
        Put(currentReading, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _currentMeasurement = Value(); Put(currentReading, _currentMeasurement, 1); measurements.Children.Add(currentReading);
        OnPolled("LOAD01.Voltage.Measurement", value => Dispatcher.BeginInvoke(() => _voltageMeasurement.Text = $"{value.Value.GetDouble():0.###} V"));
        OnPolled("LOAD01.Current.Measurement", value => Dispatcher.BeginInvoke(() => _currentMeasurement.Text = $"{value.Value.GetDouble():0.###} A"));
    }

    private async Task Apply(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_current, out var current) || current < 0) { SetStatus("负载电流必须是不小于 0 的数值。", true); return; }
        await Runtime.WriteAsync("LOAD01.Current.Setpoint", JsonSerializer.SerializeToElement(current), "manual");
        await Runtime.WriteAsync("LOAD01.Input.Enabled", JsonSerializer.SerializeToElement(_input.IsChecked == true), "manual");
        SetStatus("设定已应用。");
    }

    private async Task Read(object? sender, RoutedEventArgs e)
    {
        var measuredCurrent = await Runtime.ReadOnceAsync("LOAD01.Current.Measurement", "manual");
        _currentMeasurement.Text = $"{measuredCurrent.Value.GetDouble():0.###} A";
        if (Supports("LOAD01.Voltage.Measurement"))
        {
            var measuredVoltage = await Runtime.ReadOnceAsync("LOAD01.Voltage.Measurement", "manual");
            _voltageMeasurement.Text = $"{measuredVoltage.Value.GetDouble():0.###} V";
        }
        SetStatus("");
    }
}
