using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace TestStation.App.Themes;

public static class ResourceBootstrap
{
    // Publish dependencies before loading BAML that uses their static keys.
    public static void Initialize(System.Windows.Application application)
    {
        var dictionaries = application.Resources.MergedDictionaries;
        foreach (var source in new[] {
            "pack://application:,,,/HandyControl;component/Themes/SkinDark.xaml",
            "pack://application:,,,/HandyControl;component/Themes/Theme.xaml",
            "pack://application:,,,/Industrial.Controls;component/Themes/Controls.xaml" })
            dictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });
        var handy = dictionaries[1];
        var kit = dictionaries[2];
        foreach (var (type, key) in new (Type, object)[] {
            (typeof(Button), "KitButton"), (typeof(TextBox), "KitTextBox"), (typeof(ComboBox), typeof(ComboBox)),
            (typeof(ComboBoxItem), "KitComboBoxItem"), (typeof(ListBox), typeof(ListBox)), (typeof(ListBoxItem), "KitListBoxItem"),
            (typeof(TreeView), typeof(TreeView)), (typeof(TreeViewItem), typeof(TreeViewItem)), (typeof(DataGrid), typeof(DataGrid)),
            (typeof(DataGridCell), "KitDataGridCell"), (typeof(DataGridRow), "KitDataGridRow"), (typeof(ToolTip), typeof(ToolTip)) })
            application.Resources[$"Industrial{type.Name}Style"] = new Style(type, (Style)kit[key]);
        foreach (var (type, key) in new (Type, object)[] {
            (typeof(CheckBox), typeof(CheckBox)), (typeof(Menu), typeof(Menu)), (typeof(MenuItem), typeof(MenuItem)),
            (typeof(ContextMenu), typeof(ContextMenu)), (typeof(Expander), typeof(Expander)),
            (typeof(DataGridColumnHeader), "DataGridColumnHeaderStyle"), (typeof(ScrollBar), "ScrollBarBaseStyle"),
            (typeof(Separator), "SeparatorBaseStyle") })
            application.Resources[$"Handy{type.Name}Style"] = new Style(type, (Style)handy[key]);
        // The published Kit menu contracts provide palette/state setters but
        // leave their templates to WPF/HandyControl. Reuse the existing library
        // templates centrally so a Kit style never falls back to system chrome.
        foreach (var (type, key) in new (Type, string)[] {
            (typeof(Menu), "KitMenu"), (typeof(MenuItem), "KitMenuItem"), (typeof(ContextMenu), "KitContextMenu") })
        {
            var style = new Style(type, (Style)kit[key]);
            if (FindTemplate(style) is null && FindTemplate((Style)handy[type]) is { } template)
                style.Setters.Add(new Setter(Control.TemplateProperty, template));
            if (type == typeof(MenuItem))
            {
                // HandyControl selects popup-capable templates by MenuItem.Role.
                // Keeping only its leaf template breaks top-level dropdowns.
                foreach (var role in new[] { MenuItemRole.TopLevelHeader, MenuItemRole.TopLevelItem, MenuItemRole.SubmenuHeader })
                    if (FindRoleTemplate((Style)handy[type], role) is { } roleTemplate)
                    {
                        var trigger = new Trigger { Property = MenuItem.RoleProperty, Value = role };
                        trigger.Setters.Add(new Setter(Control.TemplateProperty, roleTemplate));
                        style.Triggers.Add(trigger);
                    }
            }
            application.Resources[$"Industrial{type.Name}Style"] = style;
        }
        application.Resources["IndustrialToolBarStyle"] = new Style(typeof(ToolBar), (Style)kit["KitToolBar"]);
        dictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/TestStation.App;component/Themes/ApplicationResources.xaml") });
    }

    private static ControlTemplate? FindTemplate(Style? style)
    {
        for (; style is not null; style = style.BasedOn)
            if (style.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == Control.TemplateProperty)?.Value is ControlTemplate template)
                return template;
        return null;
    }

    private static ControlTemplate? FindRoleTemplate(Style? style, MenuItemRole role)
    {
        for (; style is not null; style = style.BasedOn)
            foreach (var trigger in style.Triggers.OfType<Trigger>().Where(trigger => trigger.Property == MenuItem.RoleProperty && Equals(trigger.Value, role)))
                if (trigger.Setters.OfType<Setter>().FirstOrDefault(setter => setter.Property == Control.TemplateProperty)?.Value is ControlTemplate template)
                    return template;
        return null;
    }
}
