using AvalonDock.Themes;

namespace TestStation.App.Themes;

public sealed class CustomTheme : Theme
{
    public override Uri GetResourceUri() =>
        new("/TestStation.App;component/Themes/CustomTheme.xaml", UriKind.Relative);
}
