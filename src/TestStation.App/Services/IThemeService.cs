using System.IO;
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
    private readonly TestStation.Application.IStationSettings? _settings;

    public ThemeService(IIndustrialControlsThemeAdapter? industrialControls = null, TestStation.Application.IStationSettings? settings = null)
    {
        _industrialControls = industrialControls ?? new IndustrialControlsThemeAdapter();
        _resources = System.Windows.Application.Current?.Resources ?? new ResourceDictionary();
        _settings = settings;
        // Start from the saved choice; the shell applies it before showing the window.
        CurrentMode = settings is not null && Enum.TryParse<ThemeMode>(settings.Current.Theme, out var mode) ? mode : ThemeMode.Dark;
        CurrentAccent = settings is not null && Enum.TryParse<AccentPalette>(settings.Current.Accent, out var accent) ? accent : AccentPalette.Blue;
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
        // Insert only after replacing entries found above. Inserting the first
        // project theme earlier shifts handySkinIndex, overwrites that theme
        // with the skin and leaves a second, stale skin in the collection.
        if (themeIndex < 0) dictionaries.Insert(0, themeDictionary);

        // Promote every skin and project palette token, so controls in detached
        // popup/floating windows see the same resources as the application shell.
        foreach (var key in handySkin.Keys.Cast<object>())
            if (handySkin[key] is SolidColorBrush or Color) Synchronize(key, handySkin[key]);
        foreach (var key in themeDictionary.Keys.Cast<object>())
            Synchronize(key, themeDictionary[key]);

        var content = ((SolidColorBrush)_resources["ContentBrush"]).Color;
        var accentColor = GetAccentColor(mode, accent);
        SetBrush("AccentBrush", accentColor);
        SetBrush("PrimaryBrush", accentColor);
        _resources["PrimaryColor"] = accentColor;
        SetBrush("AccentHoverBrush", accentColor);
        // Selection and current-step surfaces are opaque accent tints mixed into
        // the content background. A solid accent bar hid row icons and status
        // colors; the tint keeps rows readable while still following the accent.
        var soft = Mix(accentColor, content, mode == ThemeMode.Dark ? 0.16 : 0.10);
        var selected = Mix(accentColor, content, mode == ThemeMode.Dark ? 0.24 : 0.18);
        SetBrush("AccentSoftBrush", soft);
        SetBrush("SelectedRowBrush", selected);
        _resources["SelectedRowColor"] = selected;
        var selectionText = ((SolidColorBrush)_resources["TextBrush"]).Color;
        SetBrush("SelectionTextBrush", selectionText);
        _resources["SelectionTextColor"] = selectionText;
        BindSelectionAliases();
        SetBrush("InfoBrush", accentColor);
        var window = ((SolidColorBrush)_resources["WindowBrush"]).Color;
        var surface = ((SolidColorBrush)_resources["SurfaceBrush"]).Color;
        var subtle = ((SolidColorBrush)_resources["SubtleBrush"]).Color;
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
        SetBrush("DarkPrimaryBrush", accentColor);
        // Text on solid accent faces (primary buttons). List/tree selection
        // overrides this alias locally with SelectionTextColor.
        SetBrush("TextIconBrush", ContrastForeground(accentColor));
        SetBrush("SecondaryBorderBrush", ((SolidColorBrush)_resources["BorderBrush"]).Color);
        foreach (var token in _industrialControls.GetPalette(_resources))
            SetBrush(token.Key, token.Value);
        CurrentMode = mode;
        CurrentAccent = accent;
        try { _settings?.Update(s => s with { Theme = mode.ToString(), Accent = accent.ToString() }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* theme still applies; saving is best effort */ }
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

    private static Color GetAccentColor(ThemeMode mode, AccentPalette accent)
    {
        var values = accent switch
        {
            AccentPalette.Teal => (Light: "#0F766E", Dark: "#4FD1C5"),
            AccentPalette.Purple => (Light: "#7C3AED", Dark: "#B794F4"),
            AccentPalette.Orange => (Light: "#C2410C", Dark: "#FDBA74"),
            _ => (Light: "#2563EB", Dark: "#5DA9FF")
        };
        return Parse(mode == ThemeMode.Dark ? values.Dark : values.Light);
    }

    /// <summary>Opaque blend of <paramref name="tint"/> over <paramref name="background"/>.</summary>
    private static Color Mix(Color tint, Color background, double amount)
    {
        byte Channel(byte t, byte b) => (byte)Math.Round(b + (t - b) * amount);
        return Color.FromRgb(Channel(tint.R, background.R), Channel(tint.G, background.G), Channel(tint.B, background.B));
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value);
    private static Color ContrastForeground(Color background) =>
        IndustrialControlsThemeAdapter.ContrastRatio(Colors.White, background) >=
        IndustrialControlsThemeAdapter.ContrastRatio(Colors.Black, background) ? Colors.White : Colors.Black;
}
