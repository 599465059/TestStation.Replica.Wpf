using System.Windows.Controls;
using TestStation.App.ViewModels.Protocols;
using TestStation.App.Views.Protocols;

namespace TestStation.App.Services;
public sealed class ProtocolPages(ProtocolLibraryView library, ProtocolEditorView editor, DbcBrowserView dbc, ProtocolTestView test,
    ProtocolLibraryViewModel catalog, ProtocolEditorViewModel editing, DbcBrowserViewModel databases)
{
    public IProjectSaveParticipant SaveParticipant => editing;
    public UserControl? Get(string page) => page switch { "协议库" => library, "自定义协议" => editor, "DBC 数据库" => dbc, "协议测试" => test, _ => null };
    public bool CanChangeProject() => editing.CanLeave();
    public void OpenProject(string root) { catalog.OpenProject(root); databases.OpenProject(root); }
}
