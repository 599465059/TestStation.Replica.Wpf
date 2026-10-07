using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TestStation.App;
using TestStation.App.Controls;
using TestStation.App.Services;
using TestStation.App.ViewModels.Protocols;
using TestStation.App.Views.Protocols;
using TestStation.Application.Protocols;
using TestStation.Infrastructure.Protocols;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;
internal sealed record ProtocolUiContext(ProtocolPages Pages, ProtocolLibraryViewModel Catalog, ProtocolEditorViewModel Editor,
    DbcBrowserViewModel Dbc, ProtocolTestViewModel Test);
internal static class ProtocolUiRegression
{
    public static ProtocolUiContext Create()
    {
        var service = new ProtocolLibraryService(new ProtocolRepository());
        var preview = new ProtocolPreviewService();
        var catalog = new ProtocolLibraryViewModel(service);
        var editor = new ProtocolEditorViewModel(catalog, service, new ProtocolEditingService(), preview, new DiscardProtocolDialog());
        var dbc = new DbcBrowserViewModel(new DbcService());
        var test = new ProtocolTestViewModel(service, preview, dbc);
        var dialogs = new DiscardProtocolDialog();
        return new(new ProtocolPages(new(catalog, dialogs), new(editor, dialogs), new(dbc), new(test), catalog, editor, dbc), catalog, editor, dbc, test);
    }
    public static void Verify(MainWindow window, ThemeService theme, ProtocolUiContext context)
    {
        void Navigate(string name)
        {
            NavigationSmokeHelper.Invoke(window, name); Pump();
            Require(((ContentControl)window.FindName("ProtocolHost")).Content == context.Pages.Get(name), $"{name} is not hosted in the main window");
        }
        Navigate("协议库"); context.Catalog.NewProtocolCommand.Execute(null); Pump();
        Require(context.Catalog.Protocols.Count == 1, "Protocol creation failed");
        Navigate("自定义协议");
        var view = (ProtocolEditorView)context.Pages.Get("自定义协议")!;
        var addFrame = Find<Button>(view).Single(button => button.Content?.ToString() == "新增帧");
        NativePointer.Click(addFrame.PointToScreen(new Point(addFrame.ActualWidth / 2, addFrame.ActualHeight / 2))); Pump();
        Require(context.Editor.Draft?.Frames.Count == 2 && Find<ProtocolFrameCanvas>(view).Count() == 2, "Multi-frame editor did not render both frames");
        var frameId = context.Editor.SelectedFrame!.Id;
        ProtocolFrameCanvas Canvas() => Find<ProtocolFrameCanvas>(view).Single(canvas => canvas.Frame?.Id == frameId);
        var canvas = Canvas();
        NativePointer.Click(canvas.PointToScreen(new Point(2.5 * canvas.ByteWidth, 52))); Pump();
        var palette = (ListBox)view.FindName("FieldPalette");
        var header = Find<ListBoxItem>(palette).Single(item => item.DataContext is ProtocolFieldRole { Role: "header" });
        NativePointer.Click(header.PointToScreen(new Point(50, header.ActualHeight / 2)), twice: true); Pump();
        Require(context.Editor.SelectedFrame!.Fields.Count == 1 && context.Editor.SelectedFrame.Fields[0].ByteOffset == 2, "Actual palette double-click did not add at selected byte");
        context.Editor.FieldForm.Constant = "AA";
        var data = Find<ListBoxItem>(palette).Single(item => item.DataContext is ProtocolFieldRole { Role: "data" });
        canvas = Canvas();
        NativePointer.Drag(data.PointToScreen(new Point(50, data.ActualHeight / 2)), canvas.PointToScreen(new Point(3.5 * canvas.ByteWidth, 52))); Pump();
        Require(context.Editor.SelectedFrame!.Fields.Count == 2, "Actual palette drag did not add a field");
        var dataId = context.Editor.SelectedField!.Id;
        canvas = Canvas();
        NativePointer.Drag(canvas.PointToScreen(new Point(3.5 * canvas.ByteWidth, 52)), canvas.PointToScreen(new Point(1.5 * canvas.ByteWidth, 52))); Pump();
        Require(context.Editor.SelectedFrame!.Fields.Single(item => item.Id == dataId).ByteOffset == 1, "Actual field drag did not move the block");
        context.Editor.FieldForm.ByteOffset = "2";
        context.Editor.ApplyPropertiesCommand.Execute(null);
        Require(context.Editor.SelectedFrame.Fields.Single(item => item.Id == dataId).ByteOffset == 1 && context.Editor.Status.Contains("重叠"), "Overlapping property edit was not rejected");
        context.Editor.FieldForm.ByteOffset = "1";
        context.Editor.FrameForm.Name = "响应帧 · 已命名";
        Require(context.Editor.Save(), "Editor could not save its properties"); Pump();
        var id = context.Editor.Draft!.Id;
        var tree = (TreeView)view.FindName("FrameTree");
        var root = Find<TreeViewItem>(tree).Single(item => item.DataContext is ProtocolTreeRoot);
        root.IsExpanded = true; Pump();
        var child = Find<TreeViewItem>(tree).Single(item => item.DataContext is ProtocolTreeEntry entry && entry.FrameId == frameId);
        NativePointer.Click(child.PointToScreen(new Point(45, 12))); Pump();
        Require(context.Editor.VisibleFrames.Count == 1 && context.Editor.Draft.Frames.Count == 2, "Tree frame selection modified the document");
        root = Find<TreeViewItem>(tree).Single(item => item.DataContext is ProtocolTreeRoot);
        NativePointer.Click(root.PointToScreen(new Point(65, 12))); Pump();
        Require(context.Editor.VisibleFrames.Count == 2, "Root tree selection did not display all frames");
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        foreach (var accent in new[] { AccentPalette.Blue, AccentPalette.Teal, AccentPalette.Purple, AccentPalette.Orange })
        {
            theme.Apply(mode, accent); Pump();
            foreach (var input in Find<TextBox>(view).Where(input => input.IsVisible))
                Require(((SolidColorBrush)input.Foreground).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["TextBrush"]).Color && input.ActualHeight >= 29, "Protocol input retained wrong theme or compressed");
            Capture(window, $"protocol-{mode}-{accent}");
        }
        context.Catalog.NewProtocolCommand.Execute(null); Pump();
        Require(context.Editor.Draft!.Id != id && context.Editor.SelectedField is null, "Switching to another protocol retained stale field selection");
        context.Catalog.SelectedProtocol = context.Catalog.Protocols.Single(item => item.Id == id); Pump();
        Require(context.Editor.Draft!.Frames.Count == 2, "Reopening saved protocol lost frames");
        Navigate("DBC 数据库");
        context.Dbc.Import(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.dbc")); Pump();
        context.Dbc.Import(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.dbc")); Pump();
        Require(context.Dbc.Databases.Count == 1 && context.Dbc.Rows.Count == 3, "DBC import duplication or signal rendering regression");
        var dbcView = (DbcBrowserView)context.Pages.Get("DBC 数据库")!;
        Require(((DataGrid)dbcView.FindName("SignalsGrid")).CanUserResizeColumns, "DBC column resizing disabled");
        Capture(window, "protocol-dbc");
        Navigate("协议测试");
        context.Test.SelectedTarget = context.Test.Targets.First(item => item.Frame?.Id == frameId);
        var generate = Find<Button>(context.Pages.Get("协议测试")!).Single(button => button.Content?.ToString() == "生成示例帧");
        NativePointer.Click(generate.PointToScreen(new Point(generate.ActualWidth / 2, generate.ActualHeight / 2))); Pump();
        Require(context.Test.HexInput.Contains("AA") && context.Test.Results.Count == 2, "Protocol test entry is not connected to offline preview");
        context.Test.HexInput = "GG"; context.Test.ParseCommand.Execute(null);
        Require(context.Test.Status.Contains("十六进制") && context.Test.Results.Count == 0, "Invalid hex left stale successful results");
        context.Test.SelectedTarget = context.Test.Targets.Single(item => item.Message?.Id == 256);
        context.Test.SignalValues = "{\"Speed\":123.45}"; context.Test.GenerateCommand.Execute(null);
        Require(context.Test.Results.Any(item => item.Name == "Speed" && item.State.Contains("123.45")), "DBC offline encode/decode is not connected");
        Capture(window, "protocol-offline-test");
        Console.WriteLine("PASS: protocol pages, native palette double-click/drag, field moves, overlap rejection, frame tree, saved reload, 8 theme combinations, DBC browser and offline test");
    }
    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        { var child = VisualTreeHelper.GetChild(parent, index); if (child is T item) yield return item; foreach (var nested in Find<T>(child)) yield return nested; }
    }
    private static void Pump()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(60) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.Combine("artifacts", "ui")); using var stream = File.Create(Path.Combine("artifacts", "ui", name + ".png")); encoder.Save(stream);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
