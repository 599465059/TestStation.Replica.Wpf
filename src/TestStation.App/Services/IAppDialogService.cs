using System.Windows;
using Industrial.Controls;

namespace TestStation.App.Services;

/// <summary>Shell-owned dialogs shared by the automatic and manual modules.</summary>
public interface IAppDialogService
{
    void Attach(DialogHost host);
    Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message);
    Task ShowInfoAsync(FrameworkElement owner, string title, string message);
    Task<string?> TextAsync(FrameworkElement owner, string title, string initialValue);
}
