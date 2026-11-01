using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Industrial.Controls;
using TestStation.App.Localization;
using IndustrialDialogResult = Industrial.Controls.DialogResult;
using IndustrialDialogService = Industrial.Controls.DialogService;

namespace TestStation.App.Services;
public enum ProtocolUnsavedChoice { Save, Discard, Cancel }
public interface IProtocolDialogService
{
    void Attach(DialogHost host);
    ProtocolUnsavedChoice AskUnsaved();
    Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message);
    Task ShowInfoAsync(FrameworkElement owner, string title, string message);
    Task<string?> TextAsync(FrameworkElement owner, string title, string initialValue);
}
public sealed class ProtocolDialogService : IProtocolDialogService, IAppDialogService, ISequenceDialogService, IManualWorkspaceDialogService
{
    public async Task<SequenceUnsavedChoice> AskUnsavedAsync(string name)
    {
        var result = await Host().ShowAsync(new DialogOptions {
            Title = UiText.Get("ManualWorkspaceUnsavedTitle"), PrimaryText = UiText.Save,
            SecondaryText = UiText.Discard, CloseText = UiText.Cancel
        }, Content(UiText.Format("ManualWorkspaceUnsavedPrompt", name)));
        return result switch {
            IndustrialDialogResult.Primary => SequenceUnsavedChoice.Save,
            IndustrialDialogResult.Secondary => SequenceUnsavedChoice.Discard,
            _ => SequenceUnsavedChoice.Cancel
        };
    }
    public async Task<string?> RenameAsync(string name)
    {
        var result = await new IndustrialDialogService(Host()).TextAsync(UiText.Get("ManualWorkspaceRename"), name, false, false);
        return result.Accepted ? result.Value : null;
    }
    private DialogHost? _host;
    public Task<bool> ShowDeviceEditorAsync(ViewModels.DeviceInstanceEditorViewModel model) =>
        ShowEditorAsync(UiText.Get("HwInstances"), new Views.DeviceInstanceEditorView { DataContext = model });
    public async Task<bool> ConfirmMessageAsync(string title, string message) =>
        await Host().ShowAsync(new DialogOptions { Title = title, PrimaryText = UiText.Confirm, CloseText = UiText.Cancel }, Content(message)) == IndustrialDialogResult.Primary;
    public async Task<bool> ShowEditorAsync(string title, FrameworkElement content) =>
        await Host().ShowAsync(new DialogOptions { Title = title, PrimaryText = UiText.Save, CloseText = UiText.Cancel }, content) == IndustrialDialogResult.Primary;
    public void Attach(DialogHost host) => _host = host;
    public async Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name)
    {
        var result = await Host().ShowAsync(new DialogOptions {
            Title = UiText.UnsavedSequenceTitle, PrimaryText = UiText.Save,
            SecondaryText = UiText.Discard, CloseText = UiText.Cancel
        }, Content(UiText.Format(nameof(UiText.UnsavedSequencePrompt), name)));
        return result switch {
            IndustrialDialogResult.Primary => SequenceUnsavedChoice.Save,
            IndustrialDialogResult.Secondary => SequenceUnsavedChoice.Discard,
            _ => SequenceUnsavedChoice.Cancel
        };
    }
    public async Task<bool> ConfirmDeleteSequenceAsync(string name) =>
        await Host().ShowAsync(new DialogOptions { Title = UiText.DeleteSequence,
            PrimaryText = UiText.Delete, CloseText = UiText.Cancel },
            Content(UiText.Format(nameof(UiText.DeleteSequencePrompt), name))) == IndustrialDialogResult.Primary;
    public async Task<string?> RenameSequenceAsync(string name)
    {
        var value = await new IndustrialDialogService(Host()).TextAsync(UiText.RenameSequence, name, false, false);
        return value.Accepted ? value.Value : null;
    }
    public async Task<string?> RequestNameAsync(string title, string name)
    {
        var value = await new IndustrialDialogService(Host()).TextAsync(title, name, false, false);
        return value.Accepted ? value.Value : null;
    }
    public async Task<bool> ConfirmDeleteItemAsync(string kind, string name) =>
        await Host().ShowAsync(new DialogOptions { Title = $"删除{kind}", PrimaryText = UiText.Delete, CloseText = UiText.Cancel },
            Content($"确认删除{kind}“{name}”？保存工程后生效。")) == IndustrialDialogResult.Primary;
    public async Task<bool> ConfirmStopExecutionAsync() =>
        await Host().ShowAsync(new DialogOptions { Title = UiText.ExecutionInProgress,
            PrimaryText = UiText.StopAndContinue, CloseText = UiText.Cancel }, Content(UiText.StopBeforeLeaving)) == IndustrialDialogResult.Primary;

    public async Task<IReadOnlyDictionary<string, System.Text.Json.JsonElement>?> RequestRunParametersAsync(
        string sequenceName, IReadOnlyList<TestStation.App.ViewModels.ParameterInputRow> parameters)
    {
        var errorMessage = "";
        while (true)
        {
            var panel = new StackPanel { Width = 520, Margin = new Thickness(8) };
            panel.Children.Add(Content("本次填写仅用于运行，不修改序列的参数默认值。"));
            panel.Children.Add(new ScrollViewer { Content = new Views.ParameterInputList { DataContext = parameters },
                MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            panel.Children.Add(Content(errorMessage));
            if (await Host().ShowAsync(new DialogOptions { Title = $"运行参数 — {sequenceName}",
                PrimaryText = "开始运行", CloseText = UiText.Cancel }, panel) != IndustrialDialogResult.Primary) return null;
            var values = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (var row in parameters)
            {
                try { if (row.ReadValue() is { } value) values.Add(row.Name, value); }
                catch (System.IO.InvalidDataException failure) { errors.Add(failure.Message); }
            }
            if (errors.Count == 0) return values;
            errorMessage = string.Join(Environment.NewLine, errors);
        }
    }
    public ProtocolUnsavedChoice AskUnsaved()
    {
        var task = Host().ShowAsync(new DialogOptions { Title = UiText.SwitchProtocol, PrimaryText = UiText.Save, SecondaryText = UiText.Discard, CloseText = UiText.Cancel }, Content(UiText.UnsavedProtocolPrompt));
        return Wait(task) switch
        {
            IndustrialDialogResult.Primary => ProtocolUnsavedChoice.Save,
            IndustrialDialogResult.Secondary => ProtocolUnsavedChoice.Discard,
            _ => ProtocolUnsavedChoice.Cancel
        };
    }

    public async Task<bool> ConfirmAsync(FrameworkElement owner, string title, string message)
    {
        var result = await Host(owner).ShowAsync(new DialogOptions { Title = title, PrimaryText = UiText.Confirm, CloseText = UiText.Cancel }, Content(message));
        return result == IndustrialDialogResult.Primary;
    }

    public async Task ShowInfoAsync(FrameworkElement owner, string title, string message)
        => await Host(owner).ShowAsync(new DialogOptions { Title = title, PrimaryText = UiText.Acknowledge }, Content(message));

    public async Task<string?> TextAsync(FrameworkElement owner, string title, string initialValue)
    {
        var value = await new IndustrialDialogService(Host(owner)).TextAsync(title, initialValue, false, true);
        return value.Accepted ? value.Value : null;
    }

    private static TextBlock Content(string message) => new()
    {
        Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460,
        Foreground = (Brush?)System.Windows.Application.Current.TryFindResource("TextBrush"),
        Margin = new Thickness(8)
    };

    private DialogHost Host(DependencyObject owner)
    {
        for (var current = owner; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is DialogHost host) { _host = host; return host; }
        return Host();
    }
    private DialogHost Host() => _host ?? throw new InvalidOperationException("当前应用未挂载 Industrial.Controls DialogHost");
    private static IndustrialDialogResult Wait(Task<IndustrialDialogResult> task)
    {
        if (task.IsCompleted) return task.GetAwaiter().GetResult();
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        task.GetAwaiter().OnCompleted(() => dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)));
        Dispatcher.PushFrame(frame);
        return task.GetAwaiter().GetResult();
    }
}
