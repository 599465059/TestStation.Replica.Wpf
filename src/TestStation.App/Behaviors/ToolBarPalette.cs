using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TestStation.App.Behaviors;

// KitToolBar preserves WPF's toolbar template. Its overflow part otherwise
// retains the operating-system palette even when the toolbar is themed.
public static class ToolBarPalette
{
    public static readonly DependencyProperty UseApplicationPaletteProperty = DependencyProperty.RegisterAttached(
        "UseApplicationPalette", typeof(bool), typeof(ToolBarPalette), new PropertyMetadata(false, OnChanged));

    public static bool GetUseApplicationPalette(DependencyObject owner) => (bool)owner.GetValue(UseApplicationPaletteProperty);
    public static void SetUseApplicationPalette(DependencyObject owner, bool value) => owner.SetValue(UseApplicationPaletteProperty, value);

    private static void OnChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not ToolBar toolbar) return;
        if ((bool)args.NewValue)
        {
            toolbar.Loaded += OnLoaded;
            if (toolbar.IsLoaded) Apply(toolbar);
        }
        else toolbar.Loaded -= OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => Apply((ToolBar)sender);

    private static void Apply(ToolBar toolbar)
    {
        if (toolbar.Template.FindName("OverflowButton", toolbar) is not ToggleButton overflow) return;
        overflow.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");
        overflow.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        overflow.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
    }
}
