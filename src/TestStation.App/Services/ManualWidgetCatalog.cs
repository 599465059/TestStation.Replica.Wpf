using Industrial.Controls.Instruments;
using TestStation.App.ViewModels;

namespace TestStation.App.Services;

// Canvas identifiers remain stable across library releases. In particular,
// numeric/status/command retain the types stored by existing projects.
public sealed record ManualWidgetRegistration(
    string Kind, string LibraryKey, string Name, string Icon, string Category,
    int Columns, int Rows, int MinimumColumns = 10, int MinimumRows = 3,
    bool Interactive = false, bool HasCornerMoveHandle = false, string Unit = "");

public static class ManualWidgetCatalog
{
    private static readonly InstrumentCatalog Library = InstrumentCatalog.Default();
    public static IReadOnlyList<ManualWidgetRegistration> Items { get; } =
    [
        new("readout", "numeric", "数值显示", "Numeric", "数值", 11, 2, 10, 2, Unit: "V"),
        new("numeric", "realtime", "实时数值显示", "ChartLine", "数值", 11, 2, 10, 2, Unit: "V"),
        new("input", "input", "数值输入", "Pencil", "数值", 11, 2, 10, 2, true, Unit: "V"),
        new("slider", "slider", "滑条指令", "Tune", "数值", 17, 6, 17, 6, true),
        new("range", "range", "范围滑块", "ArrowExpandHorizontal", "数值", 22, 7, 20, 7, true),
        new("toggle", "toggle", "开关", "ToggleSwitchOutline", "布尔与状态", 10, 5, 10, 5, true),
        new("status", "indicator", "状态指示灯", "LedOn", "布尔与状态", 12, 3),
        new("gauge", "gauge", "仪表盘", "Gauge", "仪表", 17, 16, 10, 8),
        new("progress", "progress", "进度条", "ProgressCheck", "仪表", 19, 3),
        new("ring", "ring", "进度环", "ChartDonut", "仪表", 12, 13, 10, 8),
        new("mode", "mode", "模式选择", "ViewWeekOutline", "操作与命令", 19, 5, 19, 5, true),
        new("command", "button", "命令按钮", "GestureTap", "操作与命令", 10, 5, 10, 5, true),
        new("trigger", "trigger", "触发按钮", "LightningBoltOutline", "操作与命令", 10, 5, 10, 5, true),
        new("serial", "serial", "串口控制台", "Console", "串口与通信", 26, 28, 24, 12, true, true),
        new("scope", "scope", "数字示波器", "Waveform", "波形", 32, 33, 26, 18, true, true),
        new("container", "container", "折叠容器", "FolderOutline", "容器", 20, 13, 12, 5, true, true)
    ];

    public static IReadOnlyList<ManualWidgetTool> Tools { get; } = Items
        .Select(item => new ManualWidgetTool(item.Kind, item.Name, item.Icon, item.Category)).ToArray();

    public static ManualWidgetRegistration Get(string kind) => Items.FirstOrDefault(item => item.Kind == kind)
        ?? throw new ArgumentException($"未知画布控件：{kind}");

    public static InstrumentControl Create(string kind) => Library.Create(Get(kind).LibraryKey);
}
