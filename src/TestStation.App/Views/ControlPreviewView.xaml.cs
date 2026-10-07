using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Industrial.Controls;
using TestStation.App.Services;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

public partial class ControlPreviewView : UserControl
{
    private readonly IThemeService _theme;

    public ControlPreviewView(ControlPreviewViewModel model, IThemeService theme)
    {
        _theme = theme;
        InitializeComponent();
        DataContext = model;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _theme.Changed += OnThemeChanged;
        RefreshDrawings(this);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _theme.Changed -= OnThemeChanged;
    private void OnThemeChanged(object? sender, EventArgs e) => RefreshDrawings(this);

    // Packaged drawing controls read application resources in OnRender. Repaint
    // on our theme notification even when their animation clock is disabled.
    private static void RefreshDrawings(DependencyObject root)
    {
        if (root is AnimatedControl control) control.InvalidateVisual();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            RefreshDrawings(VisualTreeHelper.GetChild(root, index));
    }
}
