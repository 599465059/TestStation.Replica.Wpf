using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using AvalonDock.Layout;
using AvalonDock.Layout.Serialization;
using TestStation.App.Services;
using TestStation.App.Localization;

namespace TestStation.App;

public partial class SequenceWorkspace : UserControl
{
    private string? _defaultLayout;
    private readonly Dictionary<string, object> _panelContents = [];

    public SequenceWorkspace()
    {
        InitializeComponent();
        BindDocumentTitle();
    }

    private void BindDocumentTitle()
    {
        // Layout models do not inherit the visual tree's DataContext.
        var panel = DockManager.Layout.Descendents().OfType<LayoutAnchorable>().Single(item => item.ContentId == "Steps");
        BindingOperations.SetBinding(panel, LayoutContent.TitleProperty, new Binding("DataContext.SequenceEditorTitle")
        {
            Source = this, FallbackValue = UiText.SequenceEditor, TargetNullValue = UiText.SequenceEditor
        });
    }

    public void Configure(ISequenceInteractionService interactions)
    {
        interactions.Attach(this);
        foreach (var panel in DockManager.Layout.Descendents().OfType<LayoutAnchorable>())
            _panelContents[panel.ContentId] = panel.Content;
        SaveDefaultLayout();
    }

    private void SaveDefaultLayout()
    {
        using var writer = new StringWriter();
        new XmlLayoutSerializer(DockManager).Serialize(writer);
        _defaultLayout = writer.ToString();
    }

    public void RestoreDefaultLayout()
    {
        if (_defaultLayout is null) return;
        var serializer = new XmlLayoutSerializer(DockManager);
        serializer.LayoutSerializationCallback += (_, args) =>
        {
            if (_panelContents.TryGetValue(args.Model.ContentId, out var content)) args.Content = content;
            else args.Cancel = true;
        };
        serializer.Deserialize(new StringReader(_defaultLayout));
        BindDocumentTitle();
    }
}
