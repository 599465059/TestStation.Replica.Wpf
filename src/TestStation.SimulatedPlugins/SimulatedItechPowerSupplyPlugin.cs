using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TestStation.Application;

namespace TestStation.SimulatedPlugins;

public sealed class SimulatedItechPowerSupplyPlugin : ISimulatedDevicePlugin
{
    public string Id => "simulated.itech.power-supply";
    public string Name => "模拟艾德克斯直流电源";
    public string Version => "0.1.0";

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
    [
        new("ITECH01.Voltage.Setpoint", "number", "艾德克斯电源", CanRead: true, CanWrite: true),
        new("ITECH01.Current.Setpoint", "number", "艾德克斯电源", CanRead: true, CanWrite: true),
        new("ITECH01.Voltage.Measurement", "number", "艾德克斯电源", CanRead: true, CanWrite: false),
        new("ITECH01.Current.Measurement", "number", "艾德克斯电源", CanRead: true, CanWrite: false),
        new("ITECH01.Output.Enabled", "boolean", "艾德克斯电源", CanRead: true, CanWrite: true)
    ];

    public IReadOnlyDictionary<string, JsonElement> DefaultValues { get; } = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
    {
        ["ITECH01.Voltage.Setpoint"] = JsonSerializer.SerializeToElement(12.0),
        ["ITECH01.Current.Setpoint"] = JsonSerializer.SerializeToElement(1.5),
        ["ITECH01.Voltage.Measurement"] = JsonSerializer.SerializeToElement(11.98),
        ["ITECH01.Current.Measurement"] = JsonSerializer.SerializeToElement(0.42),
        ["ITECH01.Output.Enabled"] = JsonSerializer.SerializeToElement(false)
    };

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName, IChannelRuntimeCoordinator runtime) => new ItechPowerSupplyView(runtime);

    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class ItechPowerSupplyView : SimulatedDeviceView
{
    private readonly TextBox _voltage;
    private readonly TextBox _current;
    private readonly TextBlock _voltageMeasurement;
    private readonly TextBlock _currentMeasurement;
    private readonly CheckBox _output;

    public ItechPowerSupplyView(IChannelRuntimeCoordinator runtime) : base("艾德克斯直流电源", "家族插件 · IT6720 / IT6800 / IT6900 模拟型号", runtime)
    {
        Body.Children.Add(Label("输出设定"));
        var voltage = FormGrid();
        Put(voltage, new TextBlock { Text = "电压 (V)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltage = NumberBox("12.0"); Put(voltage, _voltage, 1); Body.Children.Add(voltage);
        var current = FormGrid();
        Put(current, new TextBlock { Text = "电流 (A)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = NumberBox("1.5"); Put(current, _current, 1); Body.Children.Add(current);
        _output = new CheckBox { Content = "输出开启", Margin = new Thickness(0, 12, 0, 0) };
        _output.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Body.Children.Add(_output);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Button("应用设定", Apply, true));
        actions.Children.Add(Button("读取测量值", Read)); Body.Children.Add(actions);
        Body.Children.Add(Label("测量值（手动点击读取，不自动轮询）"));
        var readings = FormGrid();
        Put(readings, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltageMeasurement = Value("11.98 V"); Put(readings, _voltageMeasurement, 1);
        Body.Children.Add(readings);
        var currentReading = FormGrid();
        Put(currentReading, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _currentMeasurement = Value("0.42 A"); Put(currentReading, _currentMeasurement, 1);
        Body.Children.Add(currentReading);
        OnPolled("ITECH01.Voltage.Measurement", value => Dispatcher.BeginInvoke(() => _voltageMeasurement.Text = $"{value.Value.GetDouble():0.###} V"));
        OnPolled("ITECH01.Current.Measurement", value => Dispatcher.BeginInvoke(() => _currentMeasurement.Text = $"{value.Value.GetDouble():0.###} A"));
        SetStatus("模拟设备已连接；测量值每 500 ms 轮询一次。自动测试运行时自动暂停。");
    }

    private async void Apply(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_voltage, out var voltage) || voltage < 0 || !TryNumber(_current, out var current) || current < 0)
        { SetStatus("电压和电流必须是不小于 0 的数值。", true); return; }
        await Runtime.WriteAsync("ITECH01.Voltage.Setpoint", JsonSerializer.SerializeToElement(voltage), "manual");
        await Runtime.WriteAsync("ITECH01.Current.Setpoint", JsonSerializer.SerializeToElement(current), "manual");
        await Runtime.WriteAsync("ITECH01.Output.Enabled", JsonSerializer.SerializeToElement(_output.IsChecked == true), "manual");
        SetStatus($"模拟设定已写入：{voltage:0.###} V，{current:0.###} A，输出{(_output.IsChecked == true ? "开启" : "关闭")}。");
    }

    private async void Read(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_voltage, out var voltage) || !TryNumber(_current, out var current))
        { SetStatus("请先输入有效的设定值。", true); return; }
        var measuredVoltage = await Runtime.ReadOnceAsync("ITECH01.Voltage.Measurement", "manual");
        var measuredCurrent = await Runtime.ReadOnceAsync("ITECH01.Current.Measurement", "manual");
        _voltageMeasurement.Text = $"{measuredVoltage.Value.GetDouble():0.###} V";
        _currentMeasurement.Text = $"{measuredCurrent.Value.GetDouble():0.###} A";
        SetStatus("已读取一次模拟测量值。");
    }
}
