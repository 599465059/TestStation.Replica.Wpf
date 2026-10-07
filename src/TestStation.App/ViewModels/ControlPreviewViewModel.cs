using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TestStation.App.ViewModels;

public partial class ControlPreviewViewModel : ObservableObject
{
    public string LibraryVersion => "Industrial.Controls " +
        typeof(Industrial.Controls.Dial).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
    public IReadOnlyList<string> Modes { get; } = ["手动设置", "自动读取", "模拟数据"];
    public ObservableCollection<PreviewChannel> Channels { get; } =
    [
        new("输出电压", 12.8, "V", "正常"),
        new("输出电流", 3.2, "A", "正常"),
        new("设备温度", 42, "°C", "正常")
    ];

    [ObservableProperty] private string _channelName = "Power.Voltage";
    [ObservableProperty] private double _inputValue = 65;
    [ObservableProperty] private double _lowerValue = 20;
    [ObservableProperty] private double _upperValue = 80;
    [ObservableProperty] private string _selectedMode = "模拟数据";
    [ObservableProperty] private bool _isControlsEnabled = true;
    [ObservableProperty] private bool _isLoggingEnabled;
    [ObservableProperty] private Color _previewColor = Colors.DodgerBlue;
    [ObservableProperty] private string _status = "调整样例控件，检查显示与交互效果。";

    [RelayCommand]
    private void ApplyPreview() => Status = $"已确认：{ChannelName} = {InputValue:0.0}，模式：{SelectedMode}";

    [RelayCommand]
    private void ResetPreview()
    {
        ChannelName = "Power.Voltage";
        InputValue = 65;
        LowerValue = 20;
        UpperValue = 80;
        SelectedMode = "模拟数据";
        IsControlsEnabled = true;
        IsLoggingEnabled = false;
        PreviewColor = Colors.DodgerBlue;
        Status = "样例已恢复默认值。";
    }
}

public partial class PreviewChannel(string name, double value, string unit, string state) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private double _value = value;
    [ObservableProperty] private string _unit = unit;
    [ObservableProperty] private string _state = state;
}
