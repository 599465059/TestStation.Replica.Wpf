using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestStation.App.Converters;

public sealed class IndentToMarginConverter : IValueConverter
{
    public double IndentWidth { get; set; } = 22;
    public double BaseIndent { get; set; } = 8;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int depth ? new Thickness(depth * IndentWidth + BaseIndent, 0, 0, 0) : new Thickness(BaseIndent, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
