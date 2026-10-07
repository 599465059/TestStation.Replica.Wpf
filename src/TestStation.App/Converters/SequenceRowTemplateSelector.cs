using System.Windows;
using System.Windows.Controls;
using TestStation.App.ViewModels;

namespace TestStation.App.Converters;

public sealed class SequenceRowTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is not SequenceRow row || container is not FrameworkElement element) return null;
        var key = row.RowType switch
        {
            "group" => "GroupHeaderTemplate",
            "footer" => "GroupFooterTemplate",
            "branch" or "end" => "BranchTemplate",
            _ => "StepNameTemplate"
        };
        return element.FindResource(key) as DataTemplate;
    }
}
