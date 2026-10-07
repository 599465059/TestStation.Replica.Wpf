using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;
using TestStation.App.Services;

namespace TestStation.App.ViewModels.Protocols;

public partial class ProtocolEditorViewModel : ObservableObject, IProjectSaveParticipant
{
    private readonly IProtocolLibraryService _library;
    private readonly IProtocolEditingService _editing;
    private readonly IProtocolPreviewService _preview;
    private readonly IProtocolDialogService _dialogs;
    private string? _originalId;
    private string? _visibleFrameId;
    private bool _refreshing;
    private bool _saving;
    public ProtocolLibraryViewModel Catalog { get; }
    public ObservableCollection<ProtocolFramePresentation> VisibleFrames { get; } = [];
    public ObservableCollection<ProtocolTreeRoot> TreeRoots { get; } = [];
    public IReadOnlyList<ProtocolFieldRole> Roles { get; } = [
        new("header", "帧头", "CodeBraces", "ProtocolHeaderBrush"), new("id", "标识", "Identifier", "ProtocolIdBrush"),
        new("length", "长度", "Ruler", "ProtocolLengthBrush"), new("data", "数据", "Database", "ProtocolDataBrush"),
        new("checksum", "校验", "ShieldCheck", "ProtocolChecksumBrush"), new("tail", "帧尾", "CodeBrackets", "ProtocolTailBrush"),
        new("skip", "保留", "DotsHorizontal", "ProtocolSkipBrush") ];
    public string[] FrameKinds => ProtocolFrame.Kinds;
    public string[] FieldRoles => ProtocolField.Roles;
    public string[] FieldTypes => ProtocolField.Types;
    public string[] ByteOrders => ["big", "little"];
    public string[] Checksums => ["none", "sum8", "xor8", "crc8", "crc16", "modbus_crc16", "crc16_modbus"];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProtocol))] private ProtocolDefinition? _draft;
    public bool HasProtocol => Draft is not null;
    [ObservableProperty] private ProtocolFrame? _selectedFrame;
    [ObservableProperty] private ProtocolField? _selectedField;
    [ObservableProperty] private ProtocolFieldForm _fieldForm = new();
    [ObservableProperty] private ProtocolFrameForm _frameForm = new();
    [ObservableProperty] private string _protocolId = "";
    [ObservableProperty] private string _protocolName = "";
    [ObservableProperty] private string _protocolVersion = "1";
    [ObservableProperty] private string _transport = "serial";
    [ObservableProperty] private string _protocolDescription = "";
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private int _selectedOffset;
    [ObservableProperty] private double _byteWidth = 54;
    [ObservableProperty] private int _displayBytes;
    [ObservableProperty] private string _status = "选择或新建协议";
    [ObservableProperty] private string _hexInput = "";
    [ObservableProperty] private string _previewOutput = "";

    public ProtocolEditorViewModel(ProtocolLibraryViewModel catalog, IProtocolLibraryService library,
        IProtocolEditingService editing, IProtocolPreviewService preview, IProtocolDialogService dialogs)
    {
        Catalog = catalog; _library = library; _editing = editing; _preview = preview; _dialogs = dialogs;
        catalog.CanChangeSelection = CanLeave;
        catalog.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(catalog.SelectedProtocol))
            {
                if (_saving) return;
                if (!_saving && IsDirty && catalog.SelectedProtocol?.Id == _originalId) return;
                LoadSelection();
            }
        };
        PropertyChanged += (_, args) =>
        {
            if (!_refreshing && args.PropertyName is nameof(ProtocolId) or nameof(ProtocolName) or nameof(ProtocolVersion) or nameof(Transport) or nameof(ProtocolDescription)) IsDirty = true;
        };
    }
    public bool CanLeave()
    {
        if (_saving || !IsDirty) return true;
        var choice = _dialogs.AskUnsaved();
        if (choice == ProtocolUnsavedChoice.Cancel) return false;
        if (choice == ProtocolUnsavedChoice.Save) return Save();
        IsDirty = false;
        return true;
    }
    private void LoadSelection()
    {
        _originalId = Catalog.SelectedProtocol?.Id;
        SetDraft(_originalId is null ? null : _library.Get(_originalId), null, null);
        IsDirty = false;
    }
    private void Watch(INotifyPropertyChanged form) => form.PropertyChanged += (_, _) => { if (!_refreshing) IsDirty = true; };
    private void SetDraft(ProtocolDefinition? draft, string? frameId, string? fieldId)
    {
        _refreshing = true;
        try
        {
            Draft = draft;
            ProtocolId = draft?.Id ?? ""; ProtocolName = draft?.Name ?? ""; ProtocolVersion = (draft?.Version ?? 1).ToString(CultureInfo.InvariantCulture);
            Transport = draft?.Transport ?? "serial"; ProtocolDescription = draft?.Description ?? "";
            SelectedFrame = draft?.Frames.FirstOrDefault(frame => frame.Id == frameId) ?? draft?.Frames.FirstOrDefault();
            SelectedField = SelectedFrame?.Fields.FirstOrDefault(item => item.Id == fieldId);
            FrameForm = SelectedFrame is null ? new() : new(SelectedFrame);
            FieldForm = SelectedField is null ? new() : new(SelectedField);
            Watch(FrameForm); Watch(FieldForm);
            if (draft?.Frames.All(frame => frame.Id != _visibleFrameId) != false) _visibleFrameId = null;
            if (draft is null) TreeRoots.Clear();
            else
            {
                if (TreeRoots.Count == 0) TreeRoots.Add(new(draft.Name, []));
                var root = TreeRoots[0]; root.Name = draft.Name;
                for (var index = 0; index < draft.Frames.Count; index++)
                {
                    var entry = new ProtocolTreeEntry(draft.Frames[index].Name, draft.Frames[index].Id);
                    if (index >= root.Children.Count) root.Children.Add(entry);
                    else if (root.Children[index] != entry) root.Children[index] = entry;
                }
                while (root.Children.Count > draft.Frames.Count) root.Children.RemoveAt(root.Children.Count - 1);
            }
            RefreshVisible();
        }
        finally { _refreshing = false; }
    }
    private void RefreshVisible()
    {
        var frames = Draft?.Frames.Where(frame => _visibleFrameId is null || frame.Id == _visibleFrameId).ToArray() ?? [];
        for (var index = 0; index < frames.Length; index++)
        {
            if (index < VisibleFrames.Count) VisibleFrames[index].Frame = frames[index];
            else VisibleFrames.Add(new(frames[index]));
        }
        while (VisibleFrames.Count > frames.Length) VisibleFrames.RemoveAt(VisibleFrames.Count - 1);
    }
    private void CommitForms()
    {
        if (Draft is null) throw new ArgumentException("请先选择协议");
        var frameId = SelectedFrame?.Id;
        var fieldId = SelectedField?.Id;
        var frame = SelectedFrame is null ? null : FrameForm.Build(SelectedFrame);
        var item = SelectedField is null ? null : FieldForm.Build(SelectedField);
        var updated = _editing.Update(Draft, draft =>
        {
            draft.Id = ProtocolId.Trim(); draft.Name = ProtocolName.Trim(); draft.Version = FormNumbers.Integer(ProtocolVersion, "协议版本"); draft.Transport = Transport; draft.Description = ProtocolDescription;
            if (frame is not null)
            {
                var index = draft.Frames.FindIndex(value => value.Id == frameId);
                frame.Fields = draft.Frames[index].Fields;
                draft.Frames[index] = frame;
                if (item is not null)
                {
                    var fieldIndex = frame.Fields.FindIndex(value => value.Id == fieldId);
                    frame.Fields[fieldIndex] = item;
                    if (frame.LengthFieldId == fieldId) frame.LengthFieldId = item.Id;
                }
            }
        });
        if (_visibleFrameId == frameId) _visibleFrameId = frame?.Id;
        SetDraft(updated, frame?.Id, item?.Id);
    }
    public void ShowFrame(string? frameId) => Try(() =>
    {
        if (_refreshing) return;
        var previousFrame = SelectedFrame?.Id;
        if (IsDirty) CommitForms();
        if (frameId is not null && frameId == previousFrame) frameId = SelectedFrame?.Id;
        _visibleFrameId = frameId;
        if (frameId is not null) SelectField(frameId, null, 0);
        RefreshVisible();
    });
    public void SelectField(string frameId, string? fieldId, int offset) => Try(() =>
    {
        if (_refreshing) return;
        var previousFrame = SelectedFrame?.Id;
        var previousField = SelectedField?.Id;
        if (IsDirty) CommitForms();
        if (frameId == previousFrame) frameId = SelectedFrame?.Id ?? frameId;
        if (fieldId is not null && fieldId == previousField) fieldId = SelectedField?.Id;
        var frame = Draft?.Frames.FirstOrDefault(item => item.Id == frameId);
        if (frame is null) return;
        _refreshing = true;
        try
        {
            SelectedFrame = frame; SelectedField = frame.Fields.FirstOrDefault(item => item.Id == fieldId);
            FrameForm = new(frame); FieldForm = SelectedField is null ? new() : new(SelectedField);
            Watch(FrameForm); Watch(FieldForm);
            SelectedOffset = SelectedField?.EndOffset ?? offset;
        }
        finally { _refreshing = false; }
    });
    [RelayCommand] private void ApplyProperties() => Try(() => { CommitForms(); IsDirty = true; Status = "属性已应用，保存后写入工程"; });
    [RelayCommand] private void SaveProtocol() => Save();
    public bool Save()
    {
        return Try(() =>
        {
            CommitForms();
            _saving = true;
            try { var id = Draft!.Id; _library.Save(_originalId, Draft); _originalId = id; }
            finally { _saving = false; }
            Catalog.SelectedProtocol = Catalog.Protocols.FirstOrDefault(item => item.Id == _originalId);
            IsDirty = false; Status = "协议已保存";
        });
    }
    public string SaveSection => "自定义协议";
    public ProjectSaveResult SaveProjectChanges() => !HasProtocol || !IsDirty || Save()
        ? new(true) : new(false, Status);
    [RelayCommand] private void ValidateProtocol() => Try(() => { CommitForms(); Status = "协议校验通过"; });
    [RelayCommand] private void NewFrame() => Change(() =>
    {
        var frame = new ProtocolFrame { Id = "frame_" + Guid.NewGuid().ToString("N")[..8], Name = $"帧 {Draft!.Frames.Count + 1}" };
        return (_editing.AddFrame(Draft, frame), frame.Id, (string?)null);
    });
    [RelayCommand] private void CopyFrame() => Change(() =>
    {
        var updated = _editing.CopyFrame(Draft!, SelectedFrame?.Id ?? throw new ArgumentException("请选择帧"));
        return (updated, updated.Frames.Last().Id, (string?)null);
    });
    [RelayCommand] private void DeleteFrame() => Change(() => (_editing.RemoveFrame(Draft!, SelectedFrame?.Id ?? throw new ArgumentException("请选择帧")), (string?)null, (string?)null));
    [RelayCommand] private void DeleteField() => Change(() => (_editing.RemoveField(Draft!, SelectedFrame!.Id, SelectedField?.Id ?? throw new ArgumentException("请选择字段")), SelectedFrame.Id, (string?)null));
    [RelayCommand] private void FillReservedGaps() => Change(() =>
    {
        var frame = SelectedFrame ?? throw new ArgumentException("请选择帧");
        return (_editing.FillReservedGaps(Draft!, frame.Id), frame.Id, (string?)null);
    });
    public void AddField(string role, string? frameId = null, int? offset = null) => Change(() =>
    {
        var frame = Draft!.Frames.FirstOrDefault(value => value.Id == frameId) ?? SelectedFrame ?? throw new ArgumentException("请选择帧");
        var start = offset ?? SelectedOffset;
        if (offset is null)
            while (frame.Fields.Any(item => item.Enabled && start >= item.ByteOffset && start < item.EndOffset)) start++;
        var item = new ProtocolField { Id = role + "_" + Guid.NewGuid().ToString("N")[..6], Name = Roles.First(value => value.Role == role).Name,
            Role = role, ByteOffset = start, DataType = role is "header" or "tail" or "skip" ? "hex" : "uint8" };
        return (_editing.AddField(Draft, frame.Id, item), frame.Id, item.Id);
    });
    public bool CanMoveField(string frameId, string fieldId, int offset)
    {
        if (Draft is null) return false;
        try { _ = _editing.MoveField(Draft, frameId, fieldId, offset); return true; }
        catch (ArgumentException) { return false; }
    }
    public void MoveField(string frameId, string fieldId, int offset) => Change(() => (_editing.MoveField(Draft!, frameId, fieldId, offset), frameId, fieldId));
    [RelayCommand] private void GeneratePreview() => Try(() =>
    {
        CommitForms(); HexInput = string.Join(" ", _preview.Generate(SelectedFrame ?? throw new ArgumentException("请选择帧")).Select(value => value.ToString("X2")));
        ParsePreview();
    });
    [RelayCommand] private void ParsePreview() => Try(() =>
    {
        var frame = SelectedFrame ?? throw new ArgumentException("请选择帧");
        var bytes = _preview.ParseHex(HexInput);
        PreviewOutput = $"{frame.Name} · 输入 {bytes.Length} B / 帧长 {frame.CalculatedLength} B\n" +
            string.Join("\n", _preview.Parse(frame, bytes).Select(item => $"{item.Name} [{item.Offset}..{item.Offset + item.Length - 1}]  {item.Hex}  {item.State}")) +
            "\n示例预览填充常量；长度和校验规则仅保存配置，不自动推算。";
    });
    private void Change(Func<(ProtocolDefinition Protocol, string? FrameId, string? FieldId)> change) => Try(() =>
    {
        CommitForms(); var updated = change();
        if (_visibleFrameId is not null) _visibleFrameId = updated.FrameId;
        SetDraft(updated.Protocol, updated.FrameId, updated.FieldId); IsDirty = true; Status = "修改未保存";
    });
    private bool Try(Action action)
    {
        try { action(); return true; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or System.Text.Json.JsonException or OverflowException or FormatException or UnauthorizedAccessException)
        { Status = error.Message; return false; }
    }
}
