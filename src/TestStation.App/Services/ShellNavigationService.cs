namespace TestStation.App.Services;

/// <summary>
/// Lets a page ask the shell to switch to another top-level page by its page id
/// (for example "自动测试", "devices", "hardware", "插件管理"). The shell owns page
/// switching and unsaved-change checks; pages never touch other pages directly.
/// </summary>
public sealed class ShellNavigationService
{
    public event EventHandler<string>? Requested;
    public void Open(string pageId) => Requested?.Invoke(this, pageId);
}
