using CommunityToolkit.Mvvm.ComponentModel;

namespace TestStation.App.ViewModels;

/// <summary>One row in the SET/GET signal table. The same shape is sent to the OpenTAP Host.</summary>
public partial class SignalValueRow : ObservableObject
{
    [ObservableProperty] private string _target = "";
    [ObservableProperty] private string _dataType = "";
    [ObservableProperty] private string _mode = "constant";
    [ObservableProperty] private string _value = "";
    [ObservableProperty] private string _comparator = "NONE";
    [ObservableProperty] private string _expected = "";
    [ObservableProperty] private string _minimum = "";
    [ObservableProperty] private string _maximum = "";
    [ObservableProperty] private string _arrayMode = "ALL";
    [ObservableProperty] private string _tolerance = "0";
    public SignalValueRow Clone() => new()
    {
        Target = Target, DataType = DataType, Mode = Mode, Value = Value, Comparator = Comparator,
        Expected = Expected, Minimum = Minimum, Maximum = Maximum, ArrayMode = ArrayMode, Tolerance = Tolerance
    };
}
