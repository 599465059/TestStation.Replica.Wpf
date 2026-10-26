using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Can;
using TestStation.Application.Protocols;
using TestStation.App.Services;
using TestStation.Domain.Can;
using TestStation.Domain.Protocols;

namespace TestStation.App.ViewModels;

public sealed partial class CanTestViewModel : ObservableObject, IDisposable, IProjectSaveParticipant
{
    private readonly ICanSimulationService _simulation;
    private readonly IDbcService _dbc;
    private string? _projectRoot;
    private readonly Dictionary<string, DbcDatabase> _databases = new(StringComparer.OrdinalIgnoreCase);
    private DbcDatabase? _activeDatabase;
    private readonly DispatcherTimer _dbcTimer;
    private bool _started;
    public ObservableCollection<CanSignalValue> Signals { get; } = [];
    public ObservableCollection<CanMessageSnapshot> Messages { get; } = [];
    public ObservableCollection<CanCanvasWidget> Widgets { get; } = [];
    public ObservableCollection<string> DbcFiles { get; } = [];
    public ObservableCollection<string> Channels { get; } = ["模拟 CAN 通道"];
    public ObservableCollection<CanTreeNode> MessageTree { get; } = [];
    public ObservableCollection<DbcSignalRow> SelectedMessageSignals { get; } = [];
    [ObservableProperty] private string? _selectedDbc;
    [ObservableProperty] private CanTreeNode? _selectedTreeNode;
    [ObservableProperty] private DbcMessage? _selectedMessage;
    [ObservableProperty] private string _dbcPeriodMs = "1000";
    [ObservableProperty] private bool _isDbcPeriodicSending;
    [ObservableProperty] private string? _selectedChannel = "模拟 CAN 通道";
    [ObservableProperty] private string _manualId = "0x101";
    [ObservableProperty] private string _manualDlc = "8";
    [ObservableProperty] private string _manualData = "00 11 22 33 44 55 66 77";
    [ObservableProperty] private string _status = "模拟 CAN 未启动";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isDesignMode = true;
    public IRelayCommand ToggleCommand { get; }
    public IRelayCommand SendCommand { get; }
    public IRelayCommand SendDbcCommand { get; }
    public IRelayCommand ToggleDbcPeriodicCommand { get; }
    public IRelayCommand AddMonitorCommand { get; }
    public IRelayCommand AddSendCommand { get; }
    public IRelayCommand AddTreeCommand { get; }
    public IRelayCommand AddManualCommand { get; }
    public IRelayCommand ToggleDesignModeCommand { get; }
    public IRelayCommand<CanCanvasWidget> DeleteWidgetCommand { get; }
    public event EventHandler<string>? WidgetRequested;
    public string SaveSection => "CAN 测试";
    public CanTestViewModel(ICanSimulationService simulation, IDbcService dbc)
    {
        _simulation = simulation; _dbc = dbc;
        _dbcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dbcTimer.Tick += (_, _) => SendDbc();
        ToggleCommand = new RelayCommand(Toggle);
        SendCommand = new RelayCommand(Send);
        SendDbcCommand = new RelayCommand(SendDbc, () => SelectedMessage is not null);
        ToggleDbcPeriodicCommand = new RelayCommand(ToggleDbcPeriodic, () => SelectedMessage is not null);
        AddMonitorCommand = new RelayCommand(() => WidgetRequested?.Invoke(this, "signal_monitor"));
        AddSendCommand = new RelayCommand(() => WidgetRequested?.Invoke(this, "dbc_send"));
        AddTreeCommand = new RelayCommand(() => WidgetRequested?.Invoke(this, "message_tree"));
        AddManualCommand = new RelayCommand(() => WidgetRequested?.Invoke(this, "manual_send"));
        ToggleDesignModeCommand = new RelayCommand(() => IsDesignMode = !IsDesignMode);
        DeleteWidgetCommand = new RelayCommand<CanCanvasWidget>(DeleteWidget);
        _simulation.MessageReceived += OnMessage;
        foreach (var item in _simulation.Signals) Signals.Add(item);
        foreach (var item in _simulation.Messages) Messages.Add(item);
    }
    public void OpenProject(string root)
    {
        if (string.Equals(_projectRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        _projectRoot = root;
        _databases.Clear();
        LoadWorkspace(root);
        if (Widgets.Count == 0)
        {
            AddWidget("signal_monitor", 0, 0, 570, 310);
            AddWidget("message_tree", 580, 0, 390, 310);
            AddWidget("dbc_send", 0, 320, 570, 270);
            AddWidget("manual_send", 580, 320, 390, 270);
        }
        DbcFiles.Clear();
        foreach (var path in _dbc.Files(root).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            DbcFiles.Add(Path.GetFileName(path));
        SelectedDbc = DbcFiles.FirstOrDefault();
    }
    partial void OnSelectedDbcChanged(string? value) => RefreshMessageTree(value);
    partial void OnSelectedTreeNodeChanged(CanTreeNode? value)
    {
        SelectedMessage = value?.Message;
        SelectedMessageSignals.Clear();
        if (value?.Message is { } message)
            foreach (var signal in message.Signals) SelectedMessageSignals.Add(new DbcSignalRow(signal));
    }
    partial void OnSelectedMessageChanged(DbcMessage? value)
    {
        if (value is null) StopDbcPeriodic();
        OnPropertyChanged(nameof(HasSelectedMessage));
        SendDbcCommand.NotifyCanExecuteChanged();
        ToggleDbcPeriodicCommand.NotifyCanExecuteChanged();
    }
    public bool HasSelectedMessage => SelectedMessage is not null;
    public string ModeCaption => IsDesignMode ? "运行模式" : "设计模式";
    partial void OnIsDesignModeChanged(bool value)
    {
        if (!value) ClearWidgetSelection();
        OnPropertyChanged(nameof(ModeCaption));
    }
    public string DbcPeriodicCaption => IsDbcPeriodicSending ? "停止周期" : "启动周期";
    partial void OnIsDbcPeriodicSendingChanged(bool value) => OnPropertyChanged(nameof(DbcPeriodicCaption));
    partial void OnDbcPeriodMsChanged(string value)
    {
        if (int.TryParse(value, out var period) && period is >= 10 and <= 3_600_000)
            _dbcTimer.Interval = TimeSpan.FromMilliseconds(period);
    }
    private void RefreshMessageTree(string? file)
    {
        _activeDatabase = null;
        MessageTree.Clear();
        SelectedTreeNode = null;
        SelectedMessage = null;
        SelectedMessageSignals.Clear();
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(_projectRoot)) return;
        var path = Path.Combine(_projectRoot, "protocols", "dbc", file);
        if (!File.Exists(path)) path = Path.Combine(_projectRoot, "dbc", file);
        try
        {
            var database = _dbc.Load(path);
            _databases[file] = database;
            _activeDatabase = database;
            var root = new CanTreeNode(Path.GetFileName(file), "dbc");
            foreach (var message in database.Messages)
            {
                var messageNode = new CanTreeNode($"0x{message.Id:X3}  {message.Name}  ({message.Length} 字节)", "message", message);
                foreach (var signal in message.Signals)
                    messageNode.Children.Add(new CanTreeNode($"{signal.Name}  [{signal.StartBit}|{signal.Length}] {signal.Unit}", "signal", message, signal));
                root.Children.Add(messageNode);
            }
            MessageTree.Add(root);
        }
        catch (Exception ex) { Status = $"DBC 解析失败：{ex.Message}"; }
    }
    public void SelectTreeNode(CanTreeNode? node) => SelectedTreeNode = node;
    public void AddWidget(string kind)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal) { ["signal_monitor"] = "CAN 信号监视", ["dbc_send"] = "DBC 发送", ["message_tree"] = "CAN 报文树", ["manual_send"] = "手动发送" };
        var index = Widgets.Count;
        var width = kind is "signal_monitor" or "dbc_send" ? 570 : 390;
        var height = kind is "signal_monitor" or "message_tree" ? 310 : 270;
        AddWidget(kind, 20 + (index % 2) * 590, 20 + (index / 2) * 330, width, height, titles);
    }
    public void SelectWidget(CanCanvasWidget? widget)
    {
        foreach (var item in Widgets) item.IsSelected = ReferenceEquals(item, widget);
        OnPropertyChanged(nameof(Widgets));
    }
    public void ClearWidgetSelection() => SelectWidget(null);
    private void DeleteWidget(CanCanvasWidget? widget)
    {
        if (!IsDesignMode || widget is null) return;
        Widgets.Remove(widget);
        Status = $"已删除控件：{widget.Title}";
    }
    public void ResizeWidget(CanCanvasWidget widget, string edge, double dx, double dy)
    {
        if (!IsDesignMode) return;
        const int minimumWidth = 220, minimumHeight = 150;
        var left = widget.Left; var top = widget.Top; var width = widget.Width; var height = widget.Height;
        if (edge.Contains('W')) { left += (int)Math.Round(dx); width -= (int)Math.Round(dx); }
        if (edge.Contains('E')) width += (int)Math.Round(dx);
        if (edge.Contains('N')) { top += (int)Math.Round(dy); height -= (int)Math.Round(dy); }
        if (edge.Contains('S')) height += (int)Math.Round(dy);
        if (width < minimumWidth) { if (edge.Contains('W')) left -= minimumWidth - width; width = minimumWidth; }
        if (height < minimumHeight) { if (edge.Contains('N')) top -= minimumHeight - height; height = minimumHeight; }
        widget.Left = Math.Max(0, left); widget.Top = Math.Max(0, top); widget.Width = width; widget.Height = height;
        OnPropertyChanged(nameof(Widgets));
    }
    private void AddWidget(string kind, int left, int top, int width, int height, Dictionary<string, string>? titles = null)
    {
        titles ??= new Dictionary<string, string>(StringComparer.Ordinal) { ["signal_monitor"] = "CAN 信号监视", ["dbc_send"] = "DBC 发送", ["message_tree"] = "CAN 报文树", ["manual_send"] = "手动发送" };
        Widgets.Add(new CanCanvasWidget { Kind = kind, Title = titles.GetValueOrDefault(kind, "CAN 控件"), Left = left, Top = top, Width = width, Height = height });
    }
    private void LoadWorkspace(string root)
    {
        Widgets.Clear();
        var path = WorkspacePath(root);
        if (!File.Exists(path)) return;
        try
        {
            var workspace = JsonSerializer.Deserialize<CanTestWorkspace>(File.ReadAllText(path));
            if (workspace is not null) foreach (var widget in workspace.Widgets) Widgets.Add(widget);
            if (workspace?.ChannelId is { Length: > 0 } channel && Channels.Contains(channel)) SelectedChannel = channel;
        }
        catch (JsonException) { Status = "CAN 工区配置无法读取，将使用空白画布"; }
    }
    public ProjectSaveResult SaveProjectChanges()
    {
        if (_projectRoot is null) return new(true);
        try
        {
            var path = WorkspacePath(_projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var workspace = new CanTestWorkspace { DbcPath = SelectedDbc, ChannelId = SelectedChannel, Widgets = Widgets.ToList() };
            File.WriteAllText(path, JsonSerializer.Serialize(workspace, new JsonSerializerOptions { WriteIndented = true }));
            return new(true);
        }
        catch (Exception error) { return new(false, error.Message); }
    }
    private static string WorkspacePath(string root) => Path.Combine(root, "layouts", "can", "workspace.json");
    private void Toggle()
    {
        if (_started) { _simulation.Stop(); _started = false; IsRunning = false; Status = "模拟 CAN 已停止"; }
        else { _simulation.Start(); _started = true; IsRunning = true; Status = "模拟 CAN 运行中"; }
    }
    private void Send()
    {
        if (!int.TryParse(ManualDlc, out var dlc)) { Status = "DLC 必须是数字"; return; }
        _simulation.Send(ManualId, dlc, ManualData);
        Status = $"已发送 {ManualId}";
    }
    private void SendDbc()
    {
        if (SelectedMessage is not { } message) { Status = "请先在 DBC 树中选择报文"; return; }
        if (!int.TryParse(message.Length.ToString(), out var dlc)) { Status = "DBC 报文长度无效"; return; }
        var payload = new byte[dlc];
        try
        {
            foreach (var row in SelectedMessageSignals)
            {
                if (!double.TryParse(row.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    throw new ArgumentException($"信号 {row.Name} 的值无效");
                row.Signal.Encode(value, payload);
            }
            var data = string.Join(' ', payload.Select(item => item.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
            _simulation.Send($"0x{message.Id:X3}", dlc, data);
            Status = $"已发送 DBC 报文：{message.Name}";
        }
        catch (Exception error) { Status = $"DBC 发送失败：{error.Message}"; }
    }
    private void ToggleDbcPeriodic()
    {
        if (IsDbcPeriodicSending) { StopDbcPeriodic(); return; }
        if (!int.TryParse(DbcPeriodMs, out var period) || period is < 10 or > 3_600_000)
        { Status = "周期必须是 10～3600000 毫秒"; return; }
        _dbcTimer.Interval = TimeSpan.FromMilliseconds(period);
        _dbcTimer.Start();
        IsDbcPeriodicSending = true;
        Status = $"已启动 DBC 周期发送（{period} ms）";
    }
    private void StopDbcPeriodic()
    {
        _dbcTimer.Stop();
        if (IsDbcPeriodicSending)
        {
            IsDbcPeriodicSending = false;
            Status = "DBC 周期发送已停止";
        }
    }
    private void OnMessage(object? sender, CanMessageSnapshot message)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            Messages.Insert(0, message);
            while (Messages.Count > 100) Messages.RemoveAt(Messages.Count - 1);
            var decoded = DecodeSignals(message);
            Signals.Clear();
            if (decoded.Count > 0)
            {
                foreach (var signal in decoded) Signals.Add(signal);
                foreach (var row in SelectedMessageSignals)
                {
                    var current = decoded.FirstOrDefault(item => item.Name.Equals(row.Name, StringComparison.OrdinalIgnoreCase));
                    if (current is not null) row.Value = current.Value;
                }
            }
            else foreach (var signal in _simulation.Signals) Signals.Add(signal);
        });
    }
    private List<CanSignalValue> DecodeSignals(CanMessageSnapshot message)
    {
        if (_activeDatabase is null || !TryParseCanId(message.Id, out var id)) return [];
        var definition = _activeDatabase.Messages.FirstOrDefault(item => item.Id == id);
        if (definition is null) return [];
        var payload = ParsePayload(message.Data);
        var values = new List<CanSignalValue>(definition.Signals.Count);
        foreach (var signal in definition.Signals)
        {
            try
            {
                var value = signal.Decode(payload);
                values.Add(new(signal.Name, signal.Unit, value.ToString("G", System.Globalization.CultureInfo.InvariantCulture), message.Timestamp));
            }
            catch (ArgumentException) { }
        }
        return values;
    }
    private static byte[] ParsePayload(string data)
    {
        var tokens = data.Split([' ', '\t', ',', ';', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var payload = new List<byte>(tokens.Length);
        foreach (var token in tokens)
        {
            if (byte.TryParse(token, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var value)) payload.Add(value);
            else break;
        }
        return payload.ToArray();
    }
    private static bool TryParseCanId(string value, out uint id)
    {
        value = value.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id);
        return uint.TryParse(value, out id);
    }
    public void Dispose() { StopDbcPeriodic(); _simulation.MessageReceived -= OnMessage; _simulation.Stop(); }
}

public sealed class CanTreeNode
{
    public CanTreeNode(string name, string kind, DbcMessage? message = null, DbcSignal? signal = null)
    { Name = name; Kind = kind; Message = message; Signal = signal; }
    public string Name { get; }
    public string Kind { get; }
    public DbcMessage? Message { get; }
    public DbcSignal? Signal { get; }
    public ObservableCollection<CanTreeNode> Children { get; } = [];
    public bool HasChildren => Children.Count > 0;
}

public sealed class DbcSignalRow : ObservableObject
{
    private string _value;
    public DbcSignalRow(DbcSignal signal) { Signal = signal; Name = signal.Name; StartBit = signal.StartBit; Length = signal.Length; Endian = signal.LittleEndian ? "小端" : "大端"; DataType = signal.Signed ? "有符号" : "无符号"; Factor = signal.Factor; Offset = signal.Offset; Unit = signal.Unit; _value = "0"; }
    public DbcSignal Signal { get; }
    public string Name { get; }
    public int StartBit { get; }
    public int Length { get; }
    public string Endian { get; }
    public string DataType { get; }
    public double Factor { get; }
    public double Offset { get; }
    public string Unit { get; }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
}
