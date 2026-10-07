using System.Text.Json;
using System.IO;
using TestStation.Application;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;
using TestStation.Infrastructure.Protocols;
using TestStation.App.Services;
using TestStation.App.ViewModels.Protocols;

namespace TestStation.Smoke;
internal static class ProtocolRegression
{
    public static void Run()
    {
        var workspace = ProjectWorkspace.Create(Path.Combine(Path.GetTempPath(), "test-station-protocol", Guid.NewGuid().ToString("N")), "protocols");
        var repository = new ProtocolRepository();
        var library = new ProtocolLibraryService(repository);
        var editing = new ProtocolEditingService();
        var preview = new ProtocolPreviewService();
        // This fixture is serialized by the read-only Python reference model, not by C#.
        var pythonProtocol = repository.Read(Path.Combine(AppContext.BaseDirectory, "Fixtures", "python_protocol.tsproto"));
        Require(pythonProtocol.Version == 1 && pythonProtocol.Frames.Count == 2 && pythonProtocol.Metadata["demo"].GetBoolean(), "Python-generated protocol contract did not load");
        Require(preview.Generate(pythonProtocol.Frames.Single(frame => frame.Id == "response")).SequenceEqual(new byte[] { 0xAA, 0x55, 0x01, 0xD2, 0x04, 0x00, 0x0D, 0x0A }), "Python fixture constant values or multi-byte endianness changed");
        library.OpenProject(workspace.Root);
        var protocol = new ProtocolDefinition { Id = "supply", Name = "电源协议", Frames = [new() { Id = "request", Name = "请求", Fields = [
            new() { Id = "header", Name = "帧头", Role = "header", Length = 2, DataType = "hex", ConstantValue = JsonSerializer.SerializeToElement("AA 55") },
            new() { Id = "value", Name = "电压", ByteOffset = 2, Length = 2, DataType = "uint16", ByteOrder = "little", Scale = .01, ChannelId = "voltage", ConstantValue = JsonSerializer.SerializeToElement(1200) }
        ] }] };
        protocol = editing.AddFrame(protocol, new() { Id = "response", Name = "响应", Kind = "read_response", FixedLength = 8 });
        library.Save(null, protocol);
        var file = Path.Combine(workspace.Root, "protocols", "custom", "supply.tsproto");
        var loaded = repository.Read(file);
        using (var serialized = JsonDocument.Parse(File.ReadAllText(file)))
            Require(serialized.RootElement.GetProperty("protocol").GetProperty("version").GetInt32() == 1, "Protocol version must be a Python-compatible integer");
        Require(loaded.Frames.Count == 2 && loaded.Frames[0].Fields[1].ChannelId == "voltage", "Multi-frame persistence lost channel metadata");
        Require(preview.Generate(loaded.Frames[0]).SequenceEqual(new byte[] { 0xAA, 0x55, 0xB0, 0x04 }), "Python-compatible constant preview byte order failed");
        Require(preview.Parse(loaded.Frames[0], [0xAA]).All(row => row.State == "输入不足"), "Truncated preview must mark fields");
        Reject(() => preview.ParseHex("0 ZZ"), "Invalid hex accepted");
        Reject(() => editing.MoveField(loaded, "request", "value", 1), "Overlapping move accepted");
        Require(loaded.Frames[0].Fields[1].ByteOffset == 2, "Rejected edit mutated original");
        Reject(() => editing.UpdateFrame(loaded, "request", new() { Id = "request", Name = "请求", FixedLength = 1 }), "Short fixed frame accepted");
        var moved = editing.MoveField(loaded, "request", "value", 4);
        Require(moved.Frames[0].Fields[1].ByteOffset == 4 && loaded.Frames[0].Fields[1].ByteOffset == 2, "Field move was not transactional");
        var freeDrop = editing.AddField(loaded, "response", new ProtocolField { Id = "far", Name = "远端字段", ByteOffset = 12, Length = 2 });
        Require(freeDrop.Frames.Single(frame => frame.Id == "response").FixedLength >= 14, "Free-form drop did not extend the frame layout");
        var gapped = editing.Update(moved, draft =>
        {
            draft.Frames[0].Fields[0].Role = "header"; draft.Frames[0].Fields[0].ByteOffset = 0; draft.Frames[0].Fields[0].Length = 1;
            draft.Frames[0].Fields[1].Role = "tail"; draft.Frames[0].Fields[1].ByteOffset = 5; draft.Frames[0].Fields[1].Length = 1;
            draft.Frames[0].FixedLength = 6;
        });
        var filled = editing.FillReservedGaps(gapped, "request");
        Require(filled.Frames[0].Fields.Any(field => field.Role == "skip" && field.ByteOffset == 1 && field.Length == 4), "Frame header/tail gap was not filled with one reserved field");
        Reject(() => editing.FillReservedGaps(loaded, "response"), "Missing header/tail gap was silently accepted");
        moved = editing.UpdateField(moved, "request", "value", new() { Id = "renamed", Name = "值", ByteOffset = 4, Length = 2 });
        moved = editing.CopyFrame(moved, "request");
        Require(moved.Frames.Count == 3 && moved.Frames.Last().Id != "request", "Frame copy reused ID");
        var imported = library.Import(file);
        Require(imported.Id == "supply_2", "Import collision handling failed");
        var snapshot = library.Get("supply")!; snapshot.Name = "未提交";
        Require(library.Get("supply")!.Name == "电源协议", "Catalog returned mutable shared state");
        loaded.Id = "renamed_protocol"; library.Save("supply", loaded);
        Require(!File.Exists(file) && repository.Read(Path.Combine(workspace.Root, "protocols", "custom", "renamed_protocol.tsproto")).Name == "电源协议", "Protocol ID rename failed");
        library.Remove("supply_2");
        Require(!File.Exists(Path.Combine(workspace.Root, "protocols", "custom", "supply_2.tsproto")), "Protocol deletion did not remove the file");
        var aliasPath = Path.Combine(workspace.Root, "aliases.tsproto");
        File.WriteAllText(aliasPath, """
        {"format":"test-station-protocol","version":1,"protocol":{"id":"alias","name":"历史协议","frames":[{"id":"a","name":"历史帧","fields":[{"id":"x","name":"历史字段","offset":2,"field_length":2,"factor":0.5,"valueOffset":-10}]}],"metadata":{"vendor":"demo"}}}
        """);
        var alias = repository.Read(aliasPath);
        Require(alias.Frames[0].Fields[0].ByteOffset == 2 && alias.Frames[0].Fields[0].ValueOffset == -10 && alias.Metadata.ContainsKey("vendor"), "Legacy aliases/metadata lost");
        File.WriteAllText(aliasPath, """
        {"id":"legacy","version":"1.0","frames":[{"id":"frame","fields":[{"id":"field"}]}]}
        """);
        var legacy = repository.Read(aliasPath);
        Require(legacy.Version == 1 && legacy.Name == "legacy" && legacy.Frames[0].Name == "frame" && legacy.Frames[0].Fields[0].Name == "field", "Legacy versions and default names differ from the source contract");
        repository.Export(aliasPath, legacy);
        using (var serialized = JsonDocument.Parse(File.ReadAllText(aliasPath)))
            Require(serialized.RootElement.GetProperty("protocol").GetProperty("version").GetInt32() == 1, "Legacy version was not normalized on export");
        Reject(() => editing.Update(loaded, draft => draft.Version = 0), "Non-positive protocol version accepted");
        Reject(() => editing.Update(loaded, draft => { draft.Frames[0].FixedLength = 2; draft.Frames[0].Fields[1].Enabled = false; }), "Disabled field exceeded the original fixed-length contract");
        Reject(() => editing.Update(loaded, draft => { draft.Frames[0].Fields[0].Role = "checksum"; draft.Frames[0].Fields[1].Role = "checksum"; draft.Frames[0].Fields[1].Enabled = false; }), "Disabled duplicate checksum violates the original source contract");
        var disabled = editing.Update(loaded, draft => draft.Frames[0].Fields[1].Enabled = false);
        Require(disabled.Frames[0].CalculatedLength == 4, "Disabling a field must preserve the original frame extent");
        File.WriteAllText(Path.Combine(workspace.Root, "protocols", "custom", "broken.tsproto"), "invalid json");
        var partial = repository.Load(workspace.Root);
        Require(partial.Protocols.Count == 1 && partial.Errors.Count == 1, "Damaged protocol prevented valid files loading");

        var dbc = new DbcService();
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample.dbc");
        var path = dbc.Import(workspace.Root, fixture);
        Require(dbc.Import(workspace.Root, fixture) == path && dbc.Files(workspace.Root).Count == 1, "Repeated DBC import duplicated files");
        var database = dbc.Load(path);
        var message = database.Messages.Single();
        var speed = message.Signals.Single(signal => signal.Name == "Speed");
        var payload = new byte[8]; speed.Encode(123.45, payload);
        Require(Math.Abs(speed.Decode(payload) - 123.45) < 1e-9, "Intel factor/offset round trip failed");
        var temperature = message.Signals.Single(signal => signal.Name == "Temperature");
        temperature.Encode(-30, payload); Require(temperature.Decode(payload) == -30, "Signed/offset round trip failed");
        var motorola = message.Signals.Single(signal => signal.Name == "MotorolaValue");
        motorola.Encode(171, payload); Require(motorola.Decode(payload) == 171 && payload[2] == 171, "Motorola bit numbering failed");
        Reject(() => speed.Decode([0]), "Truncated DBC data accepted");
        Reject(() => speed.Encode(300, payload), "Out of range physical value accepted");
        var tree = DbcBrowserViewModel.BuildTree(database);
        Require(tree.Children.Any(node => node.Name == "发送 ECU") && tree.Children.Any(node => node.Name == "接收 ECU"), "ECU direction classification missing");
        var dbcBrowser = new DbcBrowserViewModel(dbc);
        dbcBrowser.OpenProject(workspace.Root);
        Require(dbcBrowser.Rows.Count == 3, "DBC browser did not load all signal rows");
        dbcBrowser.FilterText = "Speed";
        Require(dbcBrowser.Rows.Count == 1 && dbcBrowser.Rows[0].Signal == "Speed", "DBC signal filter did not narrow the table");
        dbcBrowser.ClearFilterCommand.Execute(null);
        Require(dbcBrowser.Rows.Count == 3, "DBC signal filter clear did not restore rows");

        var catalog = new ProtocolLibraryViewModel(library);
        var editor = new ProtocolEditorViewModel(catalog, library, editing, preview, new DiscardProtocolDialog());
        catalog.OpenProject(workspace.Root);
        var invalidPath = Path.Combine(workspace.Root, "unsupported.tsproto");
        File.WriteAllText(invalidPath, "{\"format\":\"test-station-protocol\",\"version\":2147483648,\"protocol\":{}}");
        var protocolCount = catalog.Protocols.Count;
        catalog.Import(invalidPath);
        Require(catalog.Protocols.Count == protocolCount && !string.IsNullOrWhiteSpace(catalog.Status), "Invalid version import escaped the UI error boundary");
        var draftCount = editor.Draft!.Frames.Count;
        editor.ShowFrame(editor.Draft.Frames[1].Id);
        Require(editor.VisibleFrames.Count == 1 && editor.Draft.Frames.Count == draftCount, "Frame filtering modified persisted structure");
        editor.ShowFrame(null); Require(editor.VisibleFrames.Count == draftCount, "Collection selection did not restore all frames");
        var selectedFrameId = editor.SelectedFrame!.Id;
        editor.ProtocolVersion = "1.0";
        Require(!editor.Save() && library.Get("renamed_protocol")!.Version == 1, "Invalid protocol version changed saved data");
        editor.ProtocolVersion = "2";
        editor.FrameForm.Name = "重命名后的帧"; Require(editor.Save(), "ViewModel save failed");
        Require(repository.Read(Path.Combine(workspace.Root, "protocols", "custom", "renamed_protocol.tsproto")).Version == 2, "Integer version edit did not persist");
        Require(library.Get("renamed_protocol")!.Frames.Single(frame => frame.Id == selectedFrameId).Name == "重命名后的帧", "Frame name save ignored form edits");
        catalog.NewProtocolCommand.Execute(null);
        Require(editor.Draft?.Id == catalog.SelectedProtocol?.Id && editor.SelectedField is null, "Switching protocol retained a stale field selection");
        Console.WriteLine("PASS: Python protocol contract, persistence, aliases, multi-frame editing, overlap guards, offline preview, DBC encoding/import/ECU grouping and ViewModel selection");
    }
    private static void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException(message); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
internal sealed class DiscardProtocolDialog : IProtocolDialogService
{
    public void Attach(Industrial.Controls.DialogHost host) { }
    public ProtocolUnsavedChoice AskUnsaved() => ProtocolUnsavedChoice.Discard;
    public Task<bool> ConfirmAsync(System.Windows.FrameworkElement owner, string title, string message) => Task.FromResult(true);
    public Task ShowInfoAsync(System.Windows.FrameworkElement owner, string title, string message) => Task.CompletedTask;
    public Task<string?> TextAsync(System.Windows.FrameworkElement owner, string title, string initialValue) => Task.FromResult<string?>(initialValue);
}
