using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.App.ViewModels.Protocols;

public sealed record ProtocolTestTarget(string Name, ProtocolFrame? Frame, DbcMessage? Message, bool CodingSupported = true);

public partial class ProtocolTestViewModel : ObservableObject
{
    private readonly IProtocolLibraryService _library;
    private readonly IProtocolPreviewService _preview;
    private readonly DbcBrowserViewModel _dbc;
    public ObservableCollection<ProtocolTestTarget> Targets { get; } = [];
    public ObservableCollection<ProtocolPreviewField> Results { get; } = [];
    [ObservableProperty] private ProtocolTestTarget? _selectedTarget;
    [ObservableProperty] private string _hexInput = "";
    [ObservableProperty] private string _signalValues = "{}";
    [ObservableProperty] private string _status = "离线生成与解析，无需连接硬件";
    public ProtocolTestViewModel(IProtocolLibraryService library, IProtocolPreviewService preview, DbcBrowserViewModel dbc)
    {
        _library = library; _preview = preview; _dbc = dbc;
        library.Changed += (_, _) => Reload(); dbc.Changed += (_, _) => Reload();
    }
    public void Reload()
    {
        var name = SelectedTarget?.Name;
        Targets.Clear(); Results.Clear(); HexInput = "";
        foreach (var protocol in _library.Protocols)
            foreach (var frame in protocol.Frames) Targets.Add(new($"{protocol.Name} / {frame.Name}", frame, null));
        foreach (var database in _dbc.Databases)
            foreach (var message in database.Messages) Targets.Add(new($"{System.IO.Path.GetFileName(database.Path)} / 0x{message.Id:X} {message.Name}", null, message, database.Warnings.Count == 0));
        SelectedTarget = Targets.FirstOrDefault(item => item.Name == name) ?? Targets.FirstOrDefault();
    }
    partial void OnSelectedTargetChanged(ProtocolTestTarget? value) { Results.Clear(); HexInput = ""; }
    [RelayCommand] private void Generate() => Try(() =>
    {
        byte[] bytes;
        if (SelectedTarget?.Frame is { } frame) bytes = _preview.Generate(frame);
        else if (SelectedTarget?.Message is { } message)
        {
            CheckCoding();
            var values = JsonSerializer.Deserialize<Dictionary<string, double>>(SignalValues) ?? [];
            if (values.Keys.Any(name => message.Signals.All(signal => signal.Name != name))) throw new ArgumentException("JSON 中存在未知信号名称");
            bytes = new byte[message.Length];
            foreach (var signal in message.Signals)
                if (values.TryGetValue(signal.Name, out var value)) signal.Encode(value, bytes);
        }
        else throw new ArgumentException("请选择协议帧或 DBC 报文");
        HexInput = string.Join(" ", bytes.Select(value => value.ToString("X2")));
        Parse();
    });
    [RelayCommand] private void Parse() => Try(() =>
    {
        var bytes = _preview.ParseHex(HexInput);
        var results = new List<ProtocolPreviewField>();
        if (SelectedTarget?.Frame is { } frame)
        {
            results.AddRange(_preview.Parse(frame, bytes));
            Status = $"输入 {bytes.Length} B / 帧长 {frame.CalculatedLength} B。示例填充常量；长度及校验规则仅保存配置。";
        }
        else if (SelectedTarget?.Message is { } message)
        {
            CheckCoding();
            if (bytes.Length < message.Length) throw new ArgumentException($"报文长度不足，需要 {message.Length} B");
            foreach (var signal in message.Signals) results.Add(new(signal.Name, signal.StartBit, signal.Length, signal.Signed ? "signed / bit" : "unsigned / bit", signal.Extract(bytes).ToString("X"), $"{signal.Decode(bytes):G} {signal.Unit}"));
            Status = $"0x{message.Id:X}: {message.Signals.Count} 个信号";
        }
        else throw new ArgumentException("请选择测试对象");
        Results.Clear(); foreach (var result in results) Results.Add(result);
    });
    private void CheckCoding()
    {
        if (SelectedTarget?.CodingSupported == false) throw new ArgumentException("此 DBC 含尚未支持的扩展定义，仅可浏览，暂不能离线编解码");
    }
    private void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or JsonException or OverflowException) { Results.Clear(); Status = error.Message; }
    }
}
