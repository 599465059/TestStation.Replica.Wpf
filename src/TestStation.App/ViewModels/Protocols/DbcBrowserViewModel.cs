using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.App.ViewModels.Protocols;

public sealed record DbcTreeNode(string Name, DbcDatabase Database, IReadOnlyList<uint> MessageIds, IReadOnlyList<DbcTreeNode> Children);
public sealed record DbcSignalRow(string CanId, string Message, string Transmitter, string Receivers, int Dlc, string Signal,
    int StartBit, int Length, string ByteOrder, string DataType, double Factor, double Offset, string Unit);

public partial class DbcBrowserViewModel : ObservableObject
{
    private readonly IDbcService _service;
    private string? _root;
    public event EventHandler? Changed;
    public ObservableCollection<DbcTreeNode> Tree { get; } = [];
    public ObservableCollection<DbcSignalRow> Rows { get; } = [];
    public ObservableCollection<DbcDatabase> Databases { get; } = [];
    [ObservableProperty] private DbcTreeNode? _selectedNode;
    [ObservableProperty] private string _status = "未导入 DBC";
    [ObservableProperty] private string _detailTitle = "未选择 DBC";
    [ObservableProperty] private string _filterText = "";
    public DbcBrowserViewModel(IDbcService service) => _service = service;
    public void OpenProject(string root) { _root = root; Reload(); }
    public void Import(string path)
    {
        try { if (_root is null) throw new ArgumentException("请先打开工程"); _service.Import(_root, path); Reload(); }
        catch (Exception error) when (error is ArgumentException or System.IO.IOException or FormatException or OverflowException or UnauthorizedAccessException) { Status = error.Message; }
    }
    public void Reload()
    {
        var selectedPath = SelectedNode?.Database.Path;
        Tree.Clear(); Databases.Clear(); Rows.Clear();
        var errors = new List<string>();
        foreach (var path in _root is null ? [] : _service.Files(_root))
        {
            try { var database = _service.Load(path); Databases.Add(database); Tree.Add(BuildTree(database)); }
            catch (Exception error) when (error is ArgumentException or System.IO.IOException or FormatException or OverflowException or UnauthorizedAccessException) { errors.Add($"{System.IO.Path.GetFileName(path)}: {error.Message}"); }
        }
        SelectedNode = Tree.FirstOrDefault(node => node.Database.Path == selectedPath) ?? Tree.FirstOrDefault();
        Status = errors.Count > 0 ? string.Join("; ", errors) : $"{Databases.Count} 个 DBC 文件";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    partial void OnSelectedNodeChanged(DbcTreeNode? value)
    {
        DetailTitle = value?.Name ?? "未选择 DBC";
        RefreshRows();
    }
    partial void OnFilterTextChanged(string value) => RefreshRows();
    [RelayCommand] private void ClearFilter() => FilterText = "";
    private void RefreshRows()
    {
        Rows.Clear();
        if (SelectedNode is null) return;
        var allRows = SelectedNode.Database.Messages
            .Where(item => SelectedNode.MessageIds.Contains(item.Id))
            .SelectMany(message => message.Signals.Select(signal => new DbcSignalRow(
                $"0x{message.Id:X}", message.Name, message.Transmitter, string.Join(", ", signal.Receivers), message.Length, signal.Name,
                signal.StartBit, signal.Length, signal.LittleEndian ? "小端 / Intel" : "大端 / Motorola", signal.Signed ? "有符号整数" : "无符号整数", signal.Factor, signal.Offset, signal.Unit)))
            .ToArray();
        var query = FilterText.Trim();
        var visible = query.Length == 0 ? allRows : allRows.Where(row =>
            row.CanId.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Message.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Transmitter.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Receivers.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Signal.Contains(query, StringComparison.OrdinalIgnoreCase)
            || row.Unit.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var row in visible) Rows.Add(row);
        var warning = SelectedNode.Database.Warnings.Count > 0 ? $"；{string.Join("；", SelectedNode.Database.Warnings)}" : "";
        Status = query.Length == 0
            ? $"{allRows.Length} 条信号{warning}"
            : $"筛选 {visible.Length}/{allRows.Length} 条信号{warning}";
    }
    private static bool Known(string name) => !string.IsNullOrWhiteSpace(name) && name.ToUpperInvariant() is not ("VECTOR__XXX" or "XXX");
    public static DbcTreeNode BuildTree(DbcDatabase database)
    {
        DbcTreeNode MessageNode(DbcMessage message) => new($"0x{message.Id:X}  {message.Name}", database, [message.Id], []);
        DbcTreeNode Group(string name, IEnumerable<DbcMessage> messages) { var list = messages.ToArray(); return new(name, database, list.Select(item => item.Id).ToArray(), list.Select(MessageNode).ToArray()); }
        var transmitters = database.Messages.Where(message => Known(message.Transmitter)).GroupBy(message => message.Transmitter).OrderBy(group => group.Key).Select(group => Group(group.Key, group)).ToArray();
        var receivers = database.Messages.SelectMany(message => message.Signals.SelectMany(signal => signal.Receivers)).Where(Known).Distinct().Order()
            .Select(name => Group(name, database.Messages.Where(message => message.Signals.Any(signal => signal.Receivers.Contains(name))))).ToArray();
        var children = new List<DbcTreeNode>();
        if (transmitters.Length > 0) children.Add(new("发送 ECU", database, transmitters.SelectMany(node => node.MessageIds).Distinct().ToArray(), transmitters));
        if (receivers.Length > 0) children.Add(new("接收 ECU", database, receivers.SelectMany(node => node.MessageIds).Distinct().ToArray(), receivers));
        var unclassified = database.Messages.Where(message => !Known(message.Transmitter)).ToArray();
        if (unclassified.Length > 0) children.Add(Group("未分类消息", unclassified));
        return new(System.IO.Path.GetFileName(database.Path), database, database.Messages.Select(message => message.Id).ToArray(), children);
    }
}
