using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AvalonDock;
using AvalonDock.Layout;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Localization;
using TestStation.Application;
using TestStation.App.Views;
using AppThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Smoke;

internal static class UiRegression
{
    public static void Run(bool documentsOnly = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Verify(documentsOnly); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Verify(bool documentsOnly)
    {
        var app = new System.Windows.Application();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        VerifyControlStyleBases(app);
        var theme = new ThemeService();
        if (documentsOnly) { DocumentUiRegression.Verify(theme); DocumentUiRegression.VerifyRunningClose(theme); app.Shutdown(); return; }
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: new TestSequenceDialogs());
        model.CreateProject(Path.Combine(Path.GetTempPath(), "test-station-ui", Guid.NewGuid().ToString("N")), "ui");
        model.NewSequenceCommand.Execute(null);
        model.AddStep("IF");
        var protocols = ProtocolUiRegression.Create();
        protocols.Pages.OpenProject(model.ProjectPath!);
        var window = new MainWindow(model, theme, new SequenceInteractionService(new SequenceDropTargetService()),
            new ControlPreviewView(new ControlPreviewViewModel(), theme), protocols.Pages, new DiscardProtocolDialog(), ManualCanvasRegression.CreateView());
        try
        {
            ((FrameworkElement)window.FindName("StartupView")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("WorkspaceView")).Visibility = Visibility.Visible;
            window.Show();
            Pump();
            NavigationSmokeHelper.Invoke(window, "自动测试");
            Pump();
            var view = (SequenceWorkspace)window.FindName("SequenceView");
            var grid = (DataGrid)view.FindName("StepGrid");
            VerifyToolbarScope(window, view);
            VerifyEditorWidth(view, grid);
            VerifyChineseChoices(view, model);
            window.WindowState = WindowState.Maximized;
            Pump();
            VerifyEditorWidth(view, grid);
            Require(view.IsVisible && Window.GetWindow(view) == window, "Editor is not embedded in shell");
            Require(grid.SelectionMode == DataGridSelectionMode.Extended && grid.SelectionUnit == DataGridSelectionUnit.FullRow, "Multi-selection is disabled");
            Require(((ScrollViewer)view.FindName("PropertyScroll")).ActualHeight < view.ActualHeight * 0.36, "Initial property pane takes too much editor space");
            Require(grid.FrozenColumnCount == 2 && grid.CanUserResizeColumns, "Fixed/resizable columns are missing");

            grid.SelectedItem = model.Rows.Single(row => row.Step?.Kind == "if");
            Pump();
            var check = Find<CheckBox>(grid).First(box => box.Visibility == Visibility.Visible);
            var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
            toggle.Toggle(); toggle.Toggle();
            Require(model.SelectedRow!.Enabled, "ENABLED cannot be enabled again");

            // Exercise the actual TreeView double-click route, not just the view model.
            var palette = (TreeView)view.FindName("StepPalette");
            var waitItem = Find<TreeViewItem>(palette).First(item => item.DataContext is StepPaletteItem { Kind: "WAIT" });
            palette.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = waitItem });
            Require(model.Rows.Count(row => row.Step?.Kind == "wait") == 1, "Step library double-click did not insert");
            Pump();

            window.Activate();
            Pump();
            var forItem = Find<TreeViewItem>(palette).Single(item => item.DataContext is StepPaletteItem { Kind: "FOR" });
            var conditional = model.SelectedSequence!.Steps.Single();
            var elseRow = model.Rows.Single(row => row.RowType == "branch" && row.Branch == "else");
            var elseControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(elseRow);
            NativePointer.Drag(forItem.PointToScreen(new Point(50, forItem.ActualHeight / 2)),
                elseControl.PointToScreen(new Point(200, elseControl.ActualHeight * 0.25)),
                () => VerifyInsertionIndicator(view, grid, window, "then"));
            Pump();
            Require(conditional.Children.Last().Kind == "loop", "Actual drag above ELSE did not insert FOR in THEN");
            var loop = conditional.Children.Last();
            model.AddStep("COMMENT", new("Main", loop.Id, false, 0));
            Pump();
            var loopRow = model.Rows.Single(row => row.Step == loop);
            var loopControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(loopRow);
            elseRow = model.Rows.Single(row => row.RowType == "branch" && row.Branch == "else");
            elseControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(elseRow);
            NativePointer.Drag(loopControl.PointToScreen(new Point(230, loopControl.ActualHeight / 2)),
                elseControl.PointToScreen(new Point(200, elseControl.ActualHeight * 0.75)),
                () => VerifyInsertionIndicator(view, grid, window, "else"));
            Pump();
            Require(conditional.ElseChildren.Count == 1 && conditional.ElseChildren[0] == loop && loop.Children.Count == 1 && loop.Children[0].Kind == "comment",
                $"Actual drag into ELSE lost the container subtree: ELSE={string.Join(',', conditional.ElseChildren.Select(item => item.Id))}; FOR={string.Join(',', loop.Children.Select(item => item.Id))}");
            Console.WriteLine("PASS: native mouse drags above ELSE and into ELSE with complete FOR subtree");

            loopControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Rows.Single(row => row.Step == loop));
            var setupControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Rows.Single(row => row.RowType == "group" && row.Section == "Setup"));
            NativePointer.Drag(loopControl.PointToScreen(new Point(230, loopControl.ActualHeight / 2)),
                setupControl.PointToScreen(new Point(200, setupControl.ActualHeight * 0.75)));
            Pump();
            Require(model.SelectedSequence.SetupSteps.Single() == loop && loop.Children.Count == 1, "Actual cross-section container move failed");
            var commentItem = Find<TreeViewItem>(palette).Single(item => item.DataContext is StepPaletteItem { Kind: "COMMENT" });
            commentItem.BringIntoView();
            var cleanupRow = model.Rows.Single(row => row.RowType == "group" && row.Section == "Cleanup");
            grid.ScrollIntoView(cleanupRow); Pump();
            var cleanupControl = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(cleanupRow);
            NativePointer.Drag(commentItem.PointToScreen(new Point(50, commentItem.ActualHeight / 2)),
                cleanupControl.PointToScreen(new Point(200, cleanupControl.ActualHeight * 0.75)));
            Pump();
            Require(model.SelectedSequence.CleanupSteps.Count == 1, "Actual palette drag into Cleanup failed");
            Console.WriteLine("PASS: native drags across Setup/Main/Cleanup");
            foreach (var section in new[] { "Setup", "Main", "Cleanup" })
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    var group = model.Rows.Single(row => row.RowType == "group" && row.Section == section);
                    var control = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(group);
                    grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = control });
                    Pump();
                    var visibleRows = model.Rows.Count(row => row.Section == section);
                    Require((pass == 0 ? visibleRows == 1 : visibleRows > 1)
                        && model.Rows.Single(row => row.RowType == "group" && row.Section == section).IsCollapsed == (pass == 0),
                        $"Group {section} failed to collapse/expand");
                    Require(model.SelectedRow?.RowType == "group" && model.SelectedRow.Section == section, "Group insertion target was lost");
                }
            }
            var setupCount = model.SelectedSequence.SetupSteps.Count;
            var setupHeader = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.Rows.Single(row => row.RowType == "group" && row.Section == "Setup"));
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = setupHeader });
            palette.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                { RoutedEvent = Control.MouseDoubleClickEvent, Source = commentItem });
            Require(model.SelectedSequence.SetupSteps.Count == setupCount + 1 && model.SelectedRow?.Step?.Kind == "comment", "Double-click ignored the selected Setup header");
            model.DeleteSteps([model.SelectedRow!]);
            Pump();
            Console.WriteLine("PASS: group collapse/expand and double-click insertion into selected Setup");
            VerifyKeyboard(grid, model);
            grid.SelectedItem = model.Rows.Single(row => row.Step?.Kind == "wait");
            model.ApplyStepCommand.Execute(null);
            var measuredRow = model.Rows.Single(row => row.Step?.Kind == "wait");
            measuredRow.Status = "PASSED";
            grid.SelectedItem = measuredRow;
            grid.ScrollIntoView(model.Rows[0]);
            Pump();
            VerifyTextAlignment(grid);

            foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Purple);
                Pump();
                var color = ((SolidColorBrush)app.Resources["SurfaceBrush"]).Color;
                var selectedTree = Find<TreeViewItem>(palette).First(item => item.IsSelected);
                var sequencesList = (ListBox)view.FindName("SequenceList");
                var selectedSequenceItem = Find<ListBoxItem>(sequencesList).First(item => item.IsSelected);
                Require(color == (Color)ColorConverter.ConvertFromString(mode == AppThemeMode.Light ? "#FFFFFF" : "#3A4147"), "Theme did not apply its actual palette");
                Require(((SolidColorBrush)grid.Background).Color == color, "Step table retained old background");
                var textBox = Find<TextBox>(view).Single(item => System.Windows.Automation.AutomationProperties.GetAutomationId(item) == "StepName");
                Require(((SolidColorBrush)textBox.Background).Color == color, "Input retained old skin");
                var button = Find<Button>(view).First(item => item.Content is string title && title == UiText.Apply);
                Require(((SolidColorBrush)button.Background).Color == color, "Button retained old skin");
                var themeButton = (Button)window.FindName("ThemeButton");
                Require(((SolidColorBrush)themeButton.Foreground).Color == ((SolidColorBrush)app.Resources["TextBrush"]).Color, "Titlebar icon lost its theme contrast");
                VerifyButtonStates(window, app, mode);
                VerifyUncheckedState(grid, app);
                var surfaceBefore = color;
                theme.Apply(mode, AccentPalette.Teal);
                Pump();
                Require(((SolidColorBrush)app.Resources["SurfaceBrush"]).Color == surfaceBefore, "Accent changed base theme");
                selectedTree.Focus(); Pump();
                VerifySelection(selectedTree, app, "active tree");
                selectedSequenceItem.Focus(); Pump();
                VerifySelection(selectedSequenceItem, app, "active list");
                grid.Focus(); Pump();
                VerifySelection(selectedTree, app, "inactive tree");
                VerifySelection(selectedSequenceItem, app, "inactive list");
                grid.ContextMenu!.IsOpen = true;
                Pump();
                Require(((SolidColorBrush)grid.ContextMenu.Background).Color == ((SolidColorBrush)app.Resources["MenuBackgroundBrush"]).Color, "Context menu retained old skin");
                grid.ContextMenu.IsOpen = false;
                foreach (var box in Find<ComboBox>(view).Where(item => item.IsVisible))
                    Require(((SolidColorBrush)box.Foreground).Color == ((SolidColorBrush)app.Resources["TextBrush"]).Color, "ComboBox text retained old theme");
                Capture(window, mode.ToString().ToLowerInvariant());
            }
            VerifyShellMenus(window, theme, grid);
            VerifyToolbarExecution(window, view, model);

            var dock = (DockingManager)view.FindName("DockManager");
            var library = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "StepLibrary");
            library.Float(); Pump();
            Require(dock.Layout.FloatingWindows.Count == 1 && PresentationSource.FromVisual(palette) != PresentationSource.FromVisual(window),
                $"Panel floating is unavailable: floating models={dock.Layout.FloatingWindows.Count}, actual windows={app.Windows.Count}");
            var panel = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "StepSettings");
            panel.Hide(); Pump();
            view.RestoreDefaultLayout(); Pump();
            Require(dock.Layout.Descendents().OfType<LayoutAnchorable>().Count(item => item.IsVisible) == 7, "Restore lost dock panels");
            Require(dock.Layout.FloatingWindows.Count == 0 && PresentationSource.FromVisual(palette) == PresentationSource.FromVisual(window), "Restore did not dock the floating panel");
            var scroll = (ScrollViewer)view.FindName("PropertyScroll");
            Require(scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Auto && scroll.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto, "Property overflow has no scrollbars");
            Require(Find<TextBox>(scroll).Where(item => item.IsVisible).All(item => item.ActualHeight >= 28), "Properties compressed their inputs");
            var settings = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "StepSettings");
            ((LayoutAnchorablePane)settings.Parent).DockHeight = new GridLength(100);
            Pump();
            Require(scroll.ExtentHeight > scroll.ViewportHeight && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                "Shrinking the property pane did not show its scrollbar");
            Require(Find<TextBox>(scroll).Where(item => item.IsVisible).All(item => item.ActualHeight >= 28), "Small properties pane compressed inputs");
            Console.WriteLine("PASS: live WPF shell, double-click insertion, ENABLED, repeated theme/accent changes, context menu, panel restore, property sizes");
            ControlsPreviewRegression.Verify(window, theme, model);
            ProtocolUiRegression.Verify(window, theme, protocols);
            DocumentUiRegression.Verify(theme);
            DocumentUiRegression.VerifyRunningClose(theme);
        }
        catch { Capture(window, "failure"); throw; }
        finally { window.Close(); app.Shutdown(); }
    }

    private static void VerifyChineseChoices(SequenceWorkspace view, MainViewModel model)
    {
        var dock = (DockingManager)view.FindName("DockManager");
        var variables = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Variables");
        variables.IsActive = true; variables.IsSelected = true; Pump();
        var type = Find<ComboBox>(view).Single(box => ReferenceEquals(box.ItemsSource, model.VariableTypes));
        type.IsDropDownOpen = true; Pump();
        var integer = (ComboBoxItem)type.ItemContainerGenerator.ContainerFromItem("integer");
        Require(Find<TextBlock>(integer).Any(text => text.Text == "整数"), "Variable option does not render Chinese");
        integer.IsSelected = true; type.IsDropDownOpen = false; Pump();
        Require(model.VariableType == "integer" && type.SelectedItem?.ToString() == "integer",
            $"Chinese option changed the type identifier: model={model.VariableType}, selection={type.SelectedItem}");
        model.VariableName = "limit"; model.VariableDefault = "3"; model.ApplyVariableCommand.Execute(null);
        model.SaveSequenceCommand.Execute(null);
        var saved = ProjectWorkspace.Open(model.ProjectPath!).LoadSequences().Single();
        Require(saved.Variables.Single().Name == "limit" && saved.Variables.Single().DataType == "integer"
            && saved.Variables.Single().Scope == "local" && saved.Variables.Single().Default?.GetInt64() == 3,
            "Localized variable edit changed persisted identifiers or values");
        model.DeleteVariableCommand.Execute(null);
        model.SaveSequenceCommand.Execute(null);
        var sequences = dock.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Sequences");
        sequences.IsSelected = true; sequences.IsActive = true; Pump();
        Require(model.Rows.Single(row => row.RowType == "group" && row.Section == "Main").Label == "主序列"
            && model.Rows.Single(row => row.Step?.Kind == "if").Type == "IF", "Localized section lost its internal identifier or flow keyword");
        Console.WriteLine("PASS: Chinese ComboBox selection retains English identifiers through save/reload; IF and section contracts retained");
    }

    private static void VerifyEditorWidth(SequenceWorkspace view, DataGrid grid)
    {
        var ratio = grid.ActualWidth / ((DockingManager)view.FindName("DockManager")).ActualWidth;
        Require(ratio > 0.58, $"Sequence editor too narrow before/after maximizing: {ratio:P1}");
    }

    private static void VerifyControlStyleBases(System.Windows.Application app)
    {
        var dictionaries = Dictionaries(app.Resources).ToArray();
        var kit = dictionaries.First(dictionary => dictionary.Source?.OriginalString.Contains("Industrial.Controls;component/Themes/Controls.xaml", StringComparison.Ordinal) == true);
        var handy = dictionaries.First(dictionary => dictionary.Source?.OriginalString.Contains("HandyControl;component/Themes/Theme.xaml", StringComparison.Ordinal) == true);
        foreach (var (name, key) in new (string, object)[] {
            ("Button", "KitButton"), ("TextBox", "KitTextBox"), ("ComboBox", typeof(ComboBox)),
            ("ComboBoxItem", "KitComboBoxItem"), ("ListBox", typeof(ListBox)), ("ListBoxItem", "KitListBoxItem"),
            ("TreeView", typeof(TreeView)), ("TreeViewItem", typeof(TreeViewItem)), ("DataGrid", typeof(DataGrid)),
            ("DataGridCell", "KitDataGridCell"), ("DataGridRow", "KitDataGridRow"), ("ToolTip", typeof(ToolTip)) })
        {
            var alias = (Style)app.Resources[$"Industrial{name}Style"];
            Require(ReferenceEquals(alias.BasedOn, kit[key]), $"{name} does not use the packaged Industrial.Controls style");
        }
        foreach (var (name, key) in new[] { ("Menu", "KitMenu"), ("MenuItem", "KitMenuItem"), ("ContextMenu", "KitContextMenu"), ("ToolBar", "KitToolBar") })
            Require(ReferenceEquals(((Style)app.Resources[$"Industrial{name}Style"]).BasedOn, kit[key]), $"{name} has not adopted the published Kit resource contract");
        foreach (var (name, key) in new (string, object)[] {
            ("CheckBox", typeof(CheckBox)), ("Menu", typeof(Menu)), ("MenuItem", typeof(MenuItem)),
            ("ContextMenu", typeof(ContextMenu)), ("Expander", typeof(Expander)),
            ("DataGridColumnHeader", "DataGridColumnHeaderStyle"), ("ScrollBar", "ScrollBarBaseStyle"), ("Separator", "SeparatorBaseStyle") })
            Require(ReferenceEquals(((Style)app.Resources[$"Handy{name}Style"]).BasedOn, handy[key]), $"Undocumented system style fallback: {name}");
        Console.WriteLine("PASS: Industrial.Controls style provenance for 16 types; documented HandyControl template fallbacks");
    }

    private static void VerifyToolbarScope(MainWindow window, SequenceWorkspace view)
    {
        Require(window.FindName("ShellSearch") is null && window.FindName("SearchHint") is null, "Titlebar search was not removed");
        var toolbar = (ToolBar)view.FindName("SequenceToolBar");
        var run = (Button)view.FindName("RunButton");
        Require(toolbar.IsVisible && Find<Button>(toolbar).Contains(run), "Run control is not inside the sequence toolbar");
        Require(!Find<Button>((ToolBar)window.FindName("ProjectToolBar")).Any(button => button.ToolTip is string text && text.Contains("序列")), "Project toolbar contains sequence commands");
        foreach (var name in new[] { "工程概览", "协议库", "自定义协议", "DBC 数据库", "协议测试", "控件预览" })
        {
            NavigationSmokeHelper.Invoke(window, name);
            Pump();
            Require(!toolbar.IsVisible && !run.IsVisible, $"Sequence execution toolbar leaked into {name}");
        }
        NavigationSmokeHelper.Invoke(window, "自动测试");
        Pump();
        Require(toolbar.IsVisible && run.IsVisible, "Sequence toolbar did not return after navigation");
        Require(Find<Button>(toolbar).Where(button => button.IsVisible).All(button => button.ActualHeight >= 26 && button.ActualHeight <= 30), "Sequence toolbar buttons are compressed or too large");
        Console.WriteLine("PASS: removed titlebar search; compact sequence toolbar is scoped to the automatic-test page");
    }

    private static void VerifyShellMenus(MainWindow window, ThemeService theme, DataGrid grid)
    {
        var file = (MenuItem)window.FindName("FileMenu");
        foreach (var mode in new[] { AppThemeMode.Light, AppThemeMode.Dark })
        {
            theme.Apply(mode, AccentPalette.Blue);
            foreach (var toolbar in Find<ToolBar>(window).Where(toolbar => toolbar.IsVisible))
            {
                var overflow = Find<System.Windows.Controls.Primitives.ToggleButton>(toolbar).Single(item => item.Name == "OverflowButton");
                Require(((SolidColorBrush)overflow.Background).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["SurfaceBrush"]).Color, "Toolbar overflow retained operating-system colors");
            }
            NativePointer.Click(file.PointToScreen(new Point(file.ActualWidth / 2, file.ActualHeight / 2)));
            Pump();
            // Check the detached popup while it remains open through a theme switch.
            theme.Apply(mode, AccentPalette.Teal);
            Pump();
            var visibleItems = file.Items.OfType<MenuItem>().Where(item => item.IsVisible).ToArray();
            Require(visibleItems.Length >= 3, "File dropdown did not render its submenu items");
            Require(file.ActualWidth <= 70, "Shell menu header is not compact");
            foreach (var item in visibleItems)
            {
                Require(item.ActualHeight >= 28 && item.Template is not null, "File menu has an unstyled or compressed item");
                Require(((SolidColorBrush)item.Foreground).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["TextBrush"]).Color, "Open file menu text retained an old palette");
                Require(((SolidColorBrush)item.Background).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["MenuBackgroundBrush"]).Color, "Open file menu background retained an old palette");
            }
            Capture(window, $"shell-menu-{mode.ToString().ToLowerInvariant()}");
            CapturePopup(file, $"file-menu-{mode.ToString().ToLowerInvariant()}");
            file.IsSubmenuOpen = false;
            grid.ContextMenu!.IsOpen = true;
            Pump();
            theme.Apply(mode, AccentPalette.Purple);
            Pump();
            foreach (var item in grid.ContextMenu.Items.OfType<MenuItem>())
                Require(((SolidColorBrush)item.Foreground).Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["TextBrush"]).Color, "Context menu text retained an old palette");
            CaptureElement(grid.ContextMenu, $"context-menu-{mode.ToString().ToLowerInvariant()}");
            grid.ContextMenu.IsOpen = false;
        }
        Console.WriteLine("PASS: open shell/dropdown/context menus retain packaged styles and current theme/accent colors");
    }

    private static void VerifyToolbarExecution(MainWindow window, SequenceWorkspace view, MainViewModel model)
    {
        var previous = model.SelectedSequence;
        model.NewSequenceCommand.Execute(null);
        model.AddStep("WAIT");
        model.SelectedSequence!.Steps.Single().Params["timeout_ms"] = System.Text.Json.JsonSerializer.SerializeToElement(5000);
        var run = (Button)view.FindName("RunButton");
        var pause = (Button)view.FindName("PauseButton");
        NativePointer.Click(run.PointToScreen(new Point(run.ActualWidth / 2, run.ActualHeight / 2)));
        Pump();
        Require(model.ExecutionState == "RUNNING", "Sequence toolbar run did not start the engine");
        Require(!((TreeView)view.FindName("StepPalette")).IsEnabled && !((ListBox)view.FindName("SequenceList")).IsEnabled
            && !((Button)view.FindName("UndoButton")).IsEnabled && !((Button)view.FindName("DuplicateSequenceButton")).IsEnabled,
            "Runtime did not lock library/chooser/history/management controls");
        Require(!Find<TextBox>(view).Single(box => System.Windows.Automation.AutomationProperties.GetAutomationId(box) == "StepName").IsEnabled,
            "Runtime property editor is still writable");
        NativePointer.Click(pause.PointToScreen(new Point(pause.ActualWidth / 2, pause.ActualHeight / 2)));
        Pump();
        Require(model.ExecutionState == "PAUSED" && Find<TextBlock>(pause).Any(text => text.Text == "继续"), "Pause did not change the toolbar to Continue");
        NativePointer.Click(pause.PointToScreen(new Point(pause.ActualWidth / 2, pause.ActualHeight / 2)));
        Pump();
        Require(model.ExecutionState == "RUNNING" && Find<TextBlock>(pause).Any(text => text.Text == "暂停"), "Continue did not resume the engine or restore the Pause label");
        var stop = Find<Button>(view).Single(button => button.ToolTip is string text && text == "停止当前序列");
        NativePointer.Click(stop.PointToScreen(new Point(stop.ActualWidth / 2, stop.ActualHeight / 2)));
        for (var attempt = 0; attempt < 30 && model.ExecutionState != "STOPPED"; attempt++) Pump();
        Require(model.ExecutionState == "STOPPED", "Sequence toolbar stop did not stop the engine");
        for (var attempt = 0; attempt < 30 && model.RunSequenceCommand.IsRunning; attempt++) Pump();
        Require(!model.RunSequenceCommand.IsRunning, "Stopped toolbar run never completed");
        var probe = model.SelectedSequence;
        model.SelectSequence(previous);
        model.Sequences.Remove(probe!);
        Console.WriteLine("PASS: native page-toolbar run/pause/continue/stop drives the execution engine");
    }

    private static void CapturePopup(MenuItem item, string name)
    {
        var child = item.Items.OfType<MenuItem>().First(menuItem => menuItem.IsVisible);
        if (PresentationSource.FromVisual(child)?.RootVisual is FrameworkElement root)
            CaptureElement(root, name);
    }

    private static void CaptureElement(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui");
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }

    private static IEnumerable<ResourceDictionary> Dictionaries(ResourceDictionary dictionary)
    {
        yield return dictionary;
        foreach (var child in dictionary.MergedDictionaries)
        foreach (var nested in Dictionaries(child)) yield return nested;
    }

    private static void VerifyInsertionIndicator(SequenceWorkspace view, DataGrid grid, Window window, string target)
    {
        var line = (System.Windows.Shapes.Line)view.FindName("InsertionLine");
        Require(line.IsVisible && line.X1 == 0 && Math.Abs(line.X2 - grid.ActualWidth) < 1,
            $"Insertion line loses its leading edge at {target}: {line.X1} .. {line.X2}, visible={line.Visibility}, width={grid.ActualWidth}, pointer={Mouse.DirectlyOver}");
        Capture(window, $"insertion-{target}");
    }

    private static void VerifyTextAlignment(DataGrid grid)
    {
        var checkedCells = 0;
        var checkedColumns = new HashSet<string>();
        foreach (var row in Find<DataGridRow>(grid).Where(row => row.IsVisible))
        foreach (var column in grid.Columns.OfType<DataGridTextColumn>())
        {
            if (column.GetCellContent(row) is not TextBlock { Text.Length: > 0 } text) continue;
            var bounds = text.TransformToAncestor(row).TransformBounds(new Rect(text.RenderSize));
            Require(Math.Abs(bounds.Top + bounds.Height / 2 - row.ActualHeight / 2) <= 1.1,
                $"Column {column.Header} is not vertically centered: text={bounds}, rowHeight={row.ActualHeight}");
            checkedCells++;
            checkedColumns.Add(column.Header.ToString()!);
        }
        Require(checkedCells >= 8, "Not enough visible text cells checked for vertical alignment");
        Require(checkedColumns.SetEquals(new[] { "#", UiText.Type, UiText.SettingsSummary, UiText.Status }),
            $"Alignment check missed sequence text columns: {string.Join(", ", checkedColumns)}");
        Console.WriteLine($"PASS: {checkedCells} rendered sequence text cells vertically centered");
    }

    private static void VerifyButtonStates(Window window, System.Windows.Application app, AppThemeMode mode)
    {
        var check = Find<Button>(window).Single(button => button.ToolTip is string text && text == "运行前检查");
        NativePointer.Hover(check.PointToScreen(new Point(check.ActualWidth / 2, check.ActualHeight / 2)));
        Pump();
        Require(check.IsMouseOver, "Native hover failed to reach the preflight button");
        var background = Find<Border>(check).First(border => border.Background is SolidColorBrush).Background;
        Require(((SolidColorBrush)background).Color == ((SolidColorBrush)app.Resources["HoverBrush"]).Color,
            $"Preflight hover retained a stale theme: {background}");
        Capture(window, $"hover-{mode.ToString().ToLowerInvariant()}");
        var stop = Find<Button>(window).Single(button => button.ToolTip is string text && text == "停止当前序列");
        Require(!stop.IsEnabled && ((SolidColorBrush)stop.Foreground).Color == ((SolidColorBrush)app.Resources["DisabledTextBrush"]).Color,
            "Disabled execution button retained its previous text color");
        Require(Find<TextBlock>(stop).Single(text => text.Text == "停止").Foreground == stop.Foreground,
            "Composed button text overrides its disabled foreground");
        NativePointer.Hover(window.PointToScreen(new Point(window.ActualWidth / 2, 8)));
        Pump();
    }

    private static void VerifyUncheckedState(DataGrid grid, System.Windows.Application app)
    {
        var check = Find<CheckBox>(grid).First(box => box.IsVisible && box.IsChecked == true);
        var toggle = (IToggleProvider)new CheckBoxAutomationPeer(check).GetPattern(PatternInterface.Toggle);
        toggle.Toggle();
        Pump();
        Require(check.IsChecked == false && ((SolidColorBrush)check.Background).Color ==
            ((SolidColorBrush)app.Resources["InputBackgroundBrush"]).Color, "Unchecked box retained the library's white background");
        Require(Find<Border>(check).Any(border => border.Background == check.Background),
            "Unchecked box template did not render its themed background");
        toggle.Toggle();
        Pump();
        Require(check.IsChecked == true, "The themed checkbox cannot be re-enabled");
    }

    private static void VerifyKeyboard(DataGrid grid, MainViewModel model)
    {
        Window.GetWindow(grid)?.Activate();
        var sequence = model.SelectedSequence!;
        grid.Focus();
        grid.SelectedItems.Clear();
        grid.SelectedItem = model.Rows.Single(row => row.Step?.Kind == "if");
        NativePointer.Keys(0x11, 0x43); Pump(); // Ctrl+C
        grid.SelectedItem = model.Rows.Single(row => row.RowType == "footer" && row.Section == "Setup");
        NativePointer.Keys(0x11, 0x56); Pump(); // Ctrl+V
        var copy = sequence.SetupSteps.Last();
        Require(copy.Kind == "if" && copy.Children.Single().Kind == "wait", "Keyboard copy/paste lost IF subtree");
        grid.SelectedItem = model.Rows.Single(row => row.Step == copy);
        NativePointer.Keys(0x11, 0x58); Pump(); // Ctrl+X
        Require(!sequence.SetupSteps.Contains(copy), "Keyboard cut failed");
        grid.SelectedItem = model.Rows.Single(row => row.RowType == "footer" && row.Section == "Cleanup");
        NativePointer.Keys(0x11, 0x56); Pump();
        copy = sequence.CleanupSteps.Last();
        grid.SelectedItem = model.Rows.Single(row => row.Step == copy);
        NativePointer.Keys(0x11, 0x26); Pump(); // Ctrl+Up
        Require(sequence.CleanupSteps.First() == copy, "Keyboard move up failed");
        NativePointer.Keys(0x11, 0x28); Pump(); // Ctrl+Down
        Require(sequence.CleanupSteps.Last() == copy, "Keyboard move down failed");
        grid.SelectedItems.Clear();
        grid.SelectedItems.Add(model.Rows.Single(row => row.Step == copy));
        grid.SelectedItems.Add(model.Rows.Single(row => row.Step == copy.Children.Single()));
        NativePointer.Keys(0x2E); Pump(); // Delete
        Require(!sequence.CleanupSteps.Contains(copy), "Keyboard multi-selection deletion failed");
        Console.WriteLine("PASS: native keyboard copy/cut/paste/delete and Ctrl+Up/Down");
    }

    private static void VerifySelection(FrameworkElement item, System.Windows.Application app, string description)
    {
        var border = item is TreeViewItem tree
            ? (Border)tree.Template.FindName("Header", tree)
            : Find<Border>(item).First(value => ReferenceEquals(value.TemplatedParent, item));
        Require(((SolidColorBrush)border.Background).Color == ((SolidColorBrush)app.Resources["SelectedRowBrush"]).Color,
            $"{description} retained the library's default selection color: {border.Background}; expected {app.Resources["SelectedRowBrush"]}; resolved {item.TryFindResource("PrimaryBrush")}");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(35) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Capture(Window window, string theme)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "ui");
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, $"editor-{theme}.png"));
        encoder.Save(stream);
    }

    private static IEnumerable<T> Find<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
