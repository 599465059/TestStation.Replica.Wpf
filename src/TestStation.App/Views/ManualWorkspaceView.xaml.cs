using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Industrial.Controls;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.App.ViewModels;

namespace TestStation.App.Views;

/// <summary>Adapts the published MDI control; document lifecycle stays in the view model.</summary>
public partial class ManualWorkspaceView : UserControl
{
    public ManualWorkspaceViewModel Model { get; }
    private readonly ManualInstrumentFactory _instruments;
    private readonly ManualWidgetConfigurationAdapter _configuration;
    private readonly Dictionary<ManualWorkspaceTab, Border> _documents = [];
    private readonly Dictionary<ManualWorkspaceTab, ManualTestView> _views = [];
    private bool _syncing;

    public ManualWorkspaceView(ManualWorkspaceViewModel model, ManualInstrumentFactory instruments, ManualWidgetConfigurationAdapter configuration)
    {
        Model = model; _instruments = instruments; _configuration = configuration;
        InitializeComponent(); DataContext = model;
        model.Documents.CollectionChanged += (_, _) => ReconcileDocuments();
        model.PropertyChanged += OnModelChanged;
        Workspace.DocumentActivated += (_, document) =>
        {
            if (!_syncing && _documents.FirstOrDefault(pair => pair.Value == document).Key is { } tab)
                Model.ActiveDocument = tab;
            UpdateActivity();
        };
        Workspace.DocumentClosing += OnDocumentClosing;
        Workspace.ViewChanged += (_, _) => { AdaptDocumentHeaders(); UpdateViewCaption(); UpdateActivity(); };
        Workspace.PreviewMouseLeftButtonDown += OnTabMouseDown;
        Workspace.Loaded += (_, _) => AdaptDocumentHeaders();
        IsVisibleChanged += (_, _) => UpdateActivity();
        ReconcileDocuments(); UpdateViewCaption();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Model.ActiveDocument)) ActivateDocument();
    }

    private void ReconcileDocuments()
    {
        _syncing = true;
        try
        {
            foreach (var tab in _documents.Keys.Where(tab => !Model.Documents.Contains(tab)).ToArray())
            {
                tab.PropertyChanged -= OnTabChanged;
                Workspace.CloseDocument(_documents[tab]);
                _views[tab].Dispose(); _views.Remove(tab); _documents.Remove(tab);
            }
            foreach (var tab in Model.Documents.Where(tab => !_documents.ContainsKey(tab)))
            {
                var view = new ManualTestView(tab.Model, _instruments, _configuration);
                _views.Add(tab, view);
                var document = Workspace.AddDocument(tab.Title, view);
                document.Tag = tab;
                _documents.Add(tab, document);
                tab.PropertyChanged += OnTabChanged;
            }
            AdaptDocumentHeaders();
        }
        finally { _syncing = false; }
        ActivateDocument();
    }

    private void ActivateDocument()
    {
        if (!_syncing && Model.ActiveDocument is { } tab && _documents.TryGetValue(tab, out var document))
        {
            _syncing = true;
            try { Workspace.ActivateDocument(document); }
            finally { _syncing = false; }
        }
        UpdateActivity();
    }

    private void OnTabChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ManualWorkspaceTab.Title)) AdaptDocumentHeaders();
    }

    // MdiWorkspace 1.2.0 exposes no title setter/header template. Bind its public
    // composed header elements without reflection, private state or template replacement.
    private void AdaptDocumentHeaders()
    {
        foreach (var (tab, document) in _documents)
        {
            if (document.Child is Grid layout && layout.Children.OfType<Border>().FirstOrDefault()?.Child is Grid caption)
            {
                var title = caption.Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<TextBlock>()).FirstOrDefault();
                if (title is not null) title.SetBinding(TextBlock.TextProperty, new Binding(nameof(tab.Title)) { Source = tab });
            }
        }
        if (Workspace.Content is not Grid root) return;
        foreach (var tabs in root.Children.OfType<NavigationTabs>())
        foreach (var item in tabs.Items.OfType<NavigationTabItem>())
        {
            if (item.Tag is not Border document || document.Tag is not ManualWorkspaceTab tab) continue;
            var header = new TextBlock { MinWidth = 96, ToolTip = "双击标签名重命名" };
            header.SetBinding(TextBlock.TextProperty, new Binding(nameof(tab.Title)) { Source = tab });
            item.Header = header;
        }
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is Button) return;
            if (source is NavigationTabItem { Tag: Border { Tag: ManualWorkspaceTab tab } })
            {
                if (e.OriginalSource is TextBlock) { Model.RenameDocumentCommand.Execute(tab); e.Handled = true; }
                return;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
    }

    private async void OnDocumentClosing(object? sender, Industrial.Controls.DocumentClosingEventArgs e)
    {
        if (_syncing) return;
        e.Cancel = true;
        if (_documents.FirstOrDefault(pair => pair.Value == e.Document).Key is { } tab)
            await Model.CloseDocumentCommand.ExecuteAsync(tab);
    }

    private void UpdateActivity()
    {
        foreach (var (tab, view) in _views)
            view.SetWorkspaceActive(IsVisible && (Workspace.View == WorkspaceView.Windows || tab == Model.ActiveDocument));
    }

    private void Tile_Click(object sender, RoutedEventArgs e) { Workspace.Tile(); UpdateActivity(); }
    private void Cascade_Click(object sender, RoutedEventArgs e) { Workspace.Cascade(); UpdateActivity(); }
    private void ToggleView_Click(object sender, RoutedEventArgs e)
    {
        Workspace.View = Workspace.View == WorkspaceView.Tabbed ? WorkspaceView.Windows : WorkspaceView.Tabbed;
    }
    private void UpdateViewCaption() => ViewToggleButton.Content = Workspace.View == WorkspaceView.Tabbed
        ? UiText.ManualWindowView : UiText.ManualTabbedView;
}
