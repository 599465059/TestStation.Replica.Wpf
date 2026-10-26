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

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName) => new DigitalMultimeterView();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class DigitalMultimeterView : SimulatedDeviceView
{
    private readonly TextBlock _voltage;
    private readonly TextBlock _current;
    private readonly TextBlock _continuity;

    public DigitalMultimeterView() : base("数字万用表", "模拟测量仪表 · 电压、电流和通断")
    {
        Body.Children.Add(Label("当前读数"));
        var voltage = FormGrid(); Put(voltage, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltage = Value("11.97 V"); Put(voltage, _voltage, 1); Body.Children.Add(voltage);
        var current = FormGrid(); Put(current, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = Value("0.41 A"); Put(current, _current, 1); Body.Children.Add(current);
        var continuity = FormGrid(); Put(continuity, new TextBlock { Text = "通断", VerticalAlignment = VerticalAlignment.Center }, 0);
        _continuity = Value("导通"); Put(continuity, _continuity, 1); Body.Children.Add(continuity);
        Body.Children.Add(Button("读取一次", Read, true));
        SetStatus("模拟设备已连接；自动测试运行时此手动界面不会发送指令。");
    }

    private void Read(object? sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _voltage.Text = $"{11.97 + Math.Sin(now.Second / 10d) * 0.02:0.###} V";
        _current.Text = $"{0.41 + Math.Cos(now.Second / 10d) * 0.01:0.###} A";
        _continuity.Text = now.Second % 7 == 0 ? "断开" : "导通";
        SetStatus("已读取一次模拟测量值。");
    }
}
