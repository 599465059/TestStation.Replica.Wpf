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

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName) => new ElectronicLoadView();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class ElectronicLoadView : SimulatedDeviceView
{
    private readonly TextBox _current;
    private readonly TextBlock _voltageMeasurement;
    private readonly TextBlock _currentMeasurement;
    private readonly CheckBox _input;

    public ElectronicLoadView() : base("电子负载", "家族插件 · 恒流负载模拟型号")
    {
        Body.Children.Add(Label("负载设定"));
        var current = FormGrid();
        Put(current, new TextBlock { Text = "电流 (A)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = NumberBox("0.5"); Put(current, _current, 1); Body.Children.Add(current);
        _input = new CheckBox { Content = "输入开启", Margin = new Thickness(0, 12, 0, 0) };
        _input.SetResourceReference(Control.ForegroundProperty, "TextBrush"); Body.Children.Add(_input);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        actions.Children.Add(Button("应用设定", Apply, true)); actions.Children.Add(Button("读取测量值", Read)); Body.Children.Add(actions);
        Body.Children.Add(Label("测量值（手动点击读取，不自动轮询）"));
        var voltageReading = FormGrid();
        Put(voltageReading, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltageMeasurement = Value("11.96 V"); Put(voltageReading, _voltageMeasurement, 1); Body.Children.Add(voltageReading);
        var currentReading = FormGrid();
        Put(currentReading, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _currentMeasurement = Value("0.49 A"); Put(currentReading, _currentMeasurement, 1); Body.Children.Add(currentReading);
        SetStatus("模拟设备已连接；自动测试运行时此手动界面不会发送指令。");
    }

    private void Apply(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_current, out var current) || current < 0) { SetStatus("负载电流必须是不小于 0 的数值。", true); return; }
        SetStatus($"模拟设定已写入：{current:0.###} A，输入{(_input.IsChecked == true ? "开启" : "关闭")}。");
    }

    private void Read(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_current, out var current)) { SetStatus("请先输入有效的设定值。", true); return; }
        _currentMeasurement.Text = $"{Math.Max(0, current * 0.98):0.###} A";
        _voltageMeasurement.Text = $"{Math.Max(0, 12 - current * 0.08):0.###} V";
        SetStatus("已读取一次模拟测量值。");
    }
}
