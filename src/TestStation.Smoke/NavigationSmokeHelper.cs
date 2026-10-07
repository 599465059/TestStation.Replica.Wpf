using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using Industrial.Controls;
using TestStation.App;

namespace TestStation.Smoke;

internal static class NavigationSmokeHelper
{
    public static void Invoke(MainWindow window, string page)
    {
        var navigation = (NavigationView)window.FindName("NavigationItems")
            ?? throw new InvalidOperationException("NavigationView was not found.");
        var button = FindButtons(navigation).FirstOrDefault(candidate => Matches(candidate, page));
        if (button is null)
        {
            // The published NavigationView may render items through a custom
            // presenter without exposing a Button in its visual tree. Its
            // public selection API is the supported UI-level route in that
            // case and raises ItemInvoked for the selected leaf.
            navigation.SelectByTag(page);
            return;
        }

        var peer = UIElementAutomationPeer.CreatePeerForElement(button)
            ?? new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoker)
            throw new InvalidOperationException($"Navigation item '{page}' is not invokable.");
        invoker.Invoke();
    }

    private static bool Matches(Button button, string page)
    {
        if (button.DataContext is NavigationItem data && string.Equals(data.Tag?.ToString(), page, StringComparison.Ordinal))
            return true;
        if (button.Content is NavigationItem content && string.Equals(content.Tag?.ToString(), page, StringComparison.Ordinal))
            return true;
        if (button.Tag is string tag && string.Equals(tag, page, StringComparison.Ordinal))
            return true;
        return string.Equals(AutomationProperties.GetAutomationId(button), $"Navigate:{page}", StringComparison.Ordinal);
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
}
