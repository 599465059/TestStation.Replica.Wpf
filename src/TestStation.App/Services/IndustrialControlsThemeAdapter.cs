using System.Windows;
using System.Windows.Media;

namespace TestStation.App.Services;

public interface IIndustrialControlsThemeAdapter
{
    IReadOnlyDictionary<string, Color> GetPalette(ResourceDictionary resources);
}

/// <summary>Maps the packaged controls' resource contract to the application's palette.</summary>
public sealed class IndustrialControlsThemeAdapter : IIndustrialControlsThemeAdapter
{
    public IReadOnlyDictionary<string, Color> GetPalette(ResourceDictionary resources)
    {
        Color Read(string key) => ((SolidColorBrush)resources[key]).Color;
        var accent = Read("AccentBrush");
        var surface = Read("SurfaceBrush");
        var selection = Read("SelectedRowBrush");
        return new Dictionary<string, Color>
        {
            ["PageBrush"] = Read("WindowBrush"),
            ["StrokeBrush"] = Read("BorderBrush"),
            ["MenuBrush"] = Read("MenuBackgroundBrush"),
            ["MenuHoverBrush"] = Read("HoverBrush"),
            ["SelectionBrush"] = selection,
            ["SelectionTextBrush"] = ContrastRatio(Colors.White, selection) >= ContrastRatio(Colors.Black, selection)
                ? Colors.White : Colors.Black,
            ["DangerBrush"] = Read("ErrorBrush"),
            // AccentTextBrush is consumed by selected/focused states. Resolve
            // it against the actual solid selection surface so purple and
            // teal remain readable in both light and dark themes.
            ["AccentTextBrush"] = ContrastForeground(selection),
            ["AccentForegroundBrush"] = ContrastRatio(Colors.White, accent) >= ContrastRatio(Colors.Black, accent)
                ? Colors.White : Colors.Black
        };
    }

    public static double ContrastRatio(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Color ContrastForeground(Color background) =>
        ContrastRatio(Colors.White, background) >= ContrastRatio(Colors.Black, background)
            ? Colors.White : Colors.Black;
}
