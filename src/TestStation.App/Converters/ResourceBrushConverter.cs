using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TestStation.App.Converters;
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Windows.Application.Current.TryFindResource(value?.ToString() ?? "TextBrush") ?? DependencyProperty.UnsetValue;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
