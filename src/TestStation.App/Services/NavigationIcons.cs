namespace TestStation.App.Services;

/// <summary>Icon names (MaterialDesign <c>PackIconKind</c>) shared by navigation and in-page device lists.</summary>
public static class NavigationIcons
{
    /// <summary>Family-supplied icon, or a type hint for simulated plugins, so sibling families are distinguishable.</summary>
    public static string ForFamily(IManualPluginViewProvider family)
    {
        if (family is INavigationIconSource { NavigationIcon: { Length: > 0 } icon }) return icon;
        var id = family.Id;
        if (id.Contains("power-supply", StringComparison.OrdinalIgnoreCase)) return "CurrentDc";
        if (id.Contains("electronic-load", StringComparison.OrdinalIgnoreCase)) return "PowerPlugOutline";
        if (id.Contains("multimeter", StringComparison.OrdinalIgnoreCase)) return "Gauge";
        return "Devices";
    }
}
