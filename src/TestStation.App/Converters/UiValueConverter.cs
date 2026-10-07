using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TestStation.App.Localization;

namespace TestStation.App.Converters;

public sealed class UiValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == DependencyProperty.UnsetValue || value is null) return "";
        var text = value.ToString() ?? "";
        return parameter?.ToString() switch
        {
            "State" => UiDisplay.State(text), "LogLevel" => UiDisplay.LogLevel(text),
            "LogMessage" => UiDisplay.LogMessage(text),
            "Dirty" => value is true ? UiText.Unsaved : UiText.Saved,
            _ => UiDisplay.Option(text)
        };
    }

    // ComboBoxes select their original strings; templates translate only the text.
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
