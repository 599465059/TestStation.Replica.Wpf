using System.Windows;
using System.Windows.Media;
using System.Windows.Data;
using System.ComponentModel;

namespace TestStation.App.Services;

public enum ThemeMode
{
    Light,
    Dark
}

public enum AccentPalette
{
    Blue,
    Teal,
    Purple,
    Orange
}

public interface IThemeService
{
    ThemeMode CurrentMode { get; }
    AccentPalette CurrentAccent { get; }
    event EventHandler? Changed;
    void Apply(ThemeMode mode, AccentPalette accent = AccentPalette.Blue);
}

public sealed class ThemeService : IThemeService
{
    private readonly ResourceDictionary _resources;
    private readonly Dictionary<object, PaletteColor> _paletteColors = [];
    private readonly IIndustrialControlsThemeAdapter _industrialControls;
    private bool _selectionAliasesBound;

    public ThemeService(IIndustrialControlsThemeAdapter? industrialControls = null)
    {
        _industrialControls = industrialControls ?? new IndustrialControlsThemeAdapter();
        _resources = System.Windows.Application.Current?.Resources ?? new ResourceDictionary();
        CurrentMode = ThemeMode.Dark;
        CurrentAccent = AccentPalette.Blue;
    }

    public ThemeMode CurrentMode { get; private set; }
    public AccentPalette CurrentAccent { get; private set; }
    public event EventHandler? Changed;

    public void Apply(ThemeMode mode, AccentPalette accent = AccentPalette.Blue)
    {
        var dictionaries = _resources.MergedDictionaries;
        var themeIndex = -1;
        var handySkinIndex = -1;
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? string.Empty;
            if (source.Contains("UiTheme.", StringComparison.OrdinalIgnoreCase))
            {
                themeIndex = i;
            }
            else if (source.Contains("HandyControl;component/Themes/Skin", StringComparison.OrdinalIgnoreCase))
            {
                handySkinIndex = i;
            }
        }

        var themeDictionary = new ResourceDictionary
        {
            Source = new Uri($"/TestStation.App;component/Themes/UiTheme.{mode}.xaml", UriKind.Relative)
        };
        if (themeIndex >= 0) dictionaries[themeIndex] = themeDictionary;
        else dictionaries.Insert(0, themeDictionary);

        // HandyControl ships separate resource dictionaries for its light and dark
        // control templates. Keep that skin in sync with the project's theme while
        // leaving the project-specific brushes below in charge of the palette.
        var handySkin = new ResourceDictionary
        {
            Source = new Uri(
                mode == ThemeMode.Dark
                    ? "pack://application:,,,/HandyControl;component/Themes/SkinDark.xaml"
                    : "pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml",
                UriKind.Absolute)
        };
        if (handySkinIndex >= 0) dictionaries[handySkinIndex] = handySkin;
        else dictionaries.Insert(0, handySkin);

        // Promote every skin and project palette token, so controls in detached
        // popup/floating windows see the same resources as the application shell.
        foreach (var key in handySkin.Keys.Cast<object>())
            if (handySkin[key] is SolidColorBrush or Color) Synchronize(key, handySkin[key]);
        foreach (var key in themeDictionary.Keys.Cast<object>())
            Synchronize(key, themeDictionary[key]);

        var colors = GetAccentColors(mode, accent);
        SetBrush("AccentBrush", colors.Accent);
        SetBrush("PrimaryBrush", colors.Accent);
        _resources["PrimaryColor"] = colors.Accent;
        SetBrush("AccentHoverBrush", colors.Accent);
        SetBrush("AccentSoftBrush", colors.Soft);
        // Selection is a solid accent surface. It must not be a translucent
        // soft tint mixed with the monochrome page background.
        SetBrush("SelectedRowBrush", colors.Accent);
        _resources["SelectedRowColor"] = colors.Accent;
        var selectionText = ContrastForeground(colors.Accent);
        SetBrush("SelectionTextBrush", selectionText);
        _resources["SelectionTextColor"] = selectionText;
        BindSelectionAliases();
        SetBrush("InfoBrush", colors.Accent);
        var window = ((SolidColorBrush)_resources["WindowBrush"]).Color;
        var surface = ((SolidColorBrush)_resources["SurfaceBrush"]).Color;
        var subtle = ((SolidColorBrush)_resources["SubtleBrush"]).Color;
        var content = ((SolidColorBrush)_resources["ContentBrush"]).Color;
        var input = ((SolidColorBrush)_resources["InputBackgroundBrush"]).Color;
        SetBrush("RegionBrush", surface);
        _resources["RegionColor"] = surface;
        SetBrush("BackgroundBrush", content);
        SetBrush("WindowGlassBrush", window);
        SetBrush("TitleBrush", surface);
        SetBrush("ControlBackgroundBrush", surface);
        SetBrush("ControlHoverBrush", ((SolidColorBrush)_resources["HoverBrush"]).Color);
        SetBrush("ControlPressedBrush", ((SolidColorBrush)_resources["AccentSoftBrush"]).Color);
        SetBrush("ControlDisabledBackgroundBrush", subtle);
        SetBrush("PrimaryRegionBrush", surface);
        SetBrush("ThirdlyRegionBrush", subtle);
        SetBrush("DefaultBrush", input);
        SetBrush("DarkDefaultBrush", input);
        // HandyControl's inherited hover/pressed and popup states use its own
        // palette names. Map those states as well as each control's normal face.
        SetBrush("SecondaryRegionBrush", ((SolidColorBrush)_resources["HoverBrush"]).Color);
        SetBrush("ThirdlyTextBrush", ((SolidColorBrush)_resources["MutedBrush"]).Color);
        // HandyControl's popup and disabled templates use these aliases. Keep
        // their dark-mode surfaces in the same gray family instead of allowing
        // SkinDark's near-black defaults to leak into the editor.
        var mask = mode == ThemeMode.Dark
            ? Color.FromArgb(0xA0, window.R, window.G, window.B)
            : Color.FromArgb(0x66, 0x70, 0x70, 0x70);
        SetBrush("DarkMaskBrush", mask);
        SetBrush("MaskBrush", mask);
        SetBrush("PrimaryTextBrush", ((SolidColorBrush)_resources["TextBrush"]).Color);
        SetBrush("DarkPrimaryBrush", colors.Accent);
        SetBrush("TextIconBrush", ContrastForeground(colors.Accent));
        SetBrush("SecondaryBorderBrush", ((SolidColorBrush)_resources["BorderBrush"]).Color);
        foreach (var token in _industrialControls.GetPalette(_resources))
            SetBrush(token.Key, token.Value);
        CurrentMode = mode;
        CurrentAccent = accent;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Synchronize(object key, object value)
    {
        if (value is SolidColorBrush brush) SetBrush(key, brush.Color);
        else _resources[key] = value;
    }

    private void BindSelectionAliases()
    {
        if (_selectionAliasesBound) return;
        // The library resolves item-state brushes in the style's resource scope.
        // Bind those aliases to the same live colors as our application palette;
        // a replaced skin dictionary must not leave them referencing its old colors.
        foreach (var styleKey in new[] { "AppTreeViewItemStyle", "AppListBoxItemStyle" })
        {
            var style = (Style)_resources[styleKey];
            foreach (var key in new[] { "PrimaryBrush", "DarkDefaultBrush", "TextIconBrush" })
                BindingOperations.SetBinding((SolidColorBrush)style.Resources[key], SolidColorBrush.ColorProperty,
                    new Binding(nameof(PaletteColor.Value))
                    {
                        Source = _paletteColors[key == "TextIconBrush" ? "TextBrush" : "SelectedRowBrush"]
                    });
        }
        _selectionAliasesBound = true;
    }

    private void SetBrush(object key, Color color)
    {
        // Popups can retain a resource reference while detached from the visual
        // tree. Keep palette brush identities stable to update those consumers.
        if (_paletteColors.TryGetValue(key, out var source))
        {
            source.Value = color;
            return;
        }
        source = new PaletteColor { Value = color };
        _paletteColors.Add(key, source);
        var brush = new SolidColorBrush();
        // A color binding prevents WPF from freezing a brush when sealing a
        // style. Detached popups then keep a live palette reference as well.
        BindingOperations.SetBinding(brush, SolidColorBrush.ColorProperty,
            new Binding(nameof(PaletteColor.Value)) { Source = source });
        _resources[key] = brush;
    }

    private sealed class PaletteColor : INotifyPropertyChanged
    {
        private Color _value;
        public Color Value
        {
            get => _value;
            set { _value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static (Color Accent, Color Soft, Color Selected) GetAccentColors(ThemeMode mode, AccentPalette accent)
    {
        var values = accent switch
        {
            AccentPalette.Teal => (Light: "#0F766E", Dark: "#4FD1C5", SoftLight: "#F2F2F2", SoftDark: "#2A2A2A", SelectedLight: "#0F766E", SelectedDark: "#4FD1C5"),
            AccentPalette.Purple => (Light: "#7C3AED", Dark: "#B794F4", SoftLight: "#F2F2F2", SoftDark: "#2A2A2A", SelectedLight: "#7C3AED", SelectedDark: "#B794F4"),
            AccentPalette.Orange => (Light: "#C2410C", Dark: "#FDBA74", SoftLight: "#F2F2F2", SoftDark: "#2A2A2A", SelectedLight: "#C2410C", SelectedDark: "#FDBA74"),
            _ => (Light: "#2563EB", Dark: "#5DA9FF", SoftLight: "#F2F2F2", SoftDark: "#2A2A2A", SelectedLight: "#2563EB", SelectedDark: "#5DA9FF")
        };
        return mode == ThemeMode.Dark
            ? (Parse(values.Dark), Parse(values.SoftDark), Parse(values.SelectedDark))
            : (Parse(values.Light), Parse(values.SoftLight), Parse(values.SelectedLight));
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static Color ContrastForeground(Color background) =>
        IndustrialControlsThemeAdapter.ContrastRatio(Colors.White, background) >=
        IndustrialControlsThemeAdapter.ContrastRatio(Colors.Black, background) ? Colors.White : Colors.Black;
}
