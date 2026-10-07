using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Industrial.Controls.Instruments;
using TestStation.App.Localization;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.ViewModels;

public sealed class ManualWidgetViewModel(ManualWidgetDefinition model, int gridSize) : ObservableObject
{
    public ManualWidgetDefinition Model { get; private set; } = model;
    private GridBounds _bounds = model.Bounds;
    private bool _selected;
    private string _value = "12.000";
    private string _name = model.Name;
    private string _unit = model.Unit;
    private string _channel = "CH1";
    private string _command = string.Empty;
    private string _precision = "3";
    private string _interval = "100";
    private string _minimum = "0";
    private string _maximum = "100";
    private string _step = "0.1";
    private string _warning = "75";
    private string _error = "90";
    private string _encoding = "ABC";
    private string _repeat = "0";
    private string _commandInterval = "100";
    private string _serialMode = "ABC";
    private bool _showRx = true, _showTx = true, _showTime = true, _ansi = true, _autoScroll = true;
    public string Id => Model.Id;
    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Unit { get => _unit; set => SetProperty(ref _unit, value); }
    public string Channel { get => _channel; set => SetProperty(ref _channel, value); }
    public string Command { get => _command; set => SetProperty(ref _command, value); }
    public string Precision { get => _precision; set => SetProperty(ref _precision, value); }
    public string PollInterval { get => _interval; set => SetProperty(ref _interval, value); }
    public string Minimum { get => _minimum; set => SetProperty(ref _minimum, value); }
    public string Maximum { get => _maximum; set => SetProperty(ref _maximum, value); }
    public string Step { get => _step; set => SetProperty(ref _step, value); }
    public string WarningAbove { get => _warning; set => SetProperty(ref _warning, value); }
    public string ErrorAbove { get => _error; set => SetProperty(ref _error, value); }
    public string Encoding { get => _encoding; set => SetProperty(ref _encoding, value); }
    public string Repeat { get => _repeat; set => SetProperty(ref _repeat, value); }
    public string CommandInterval { get => _commandInterval; set => SetProperty(ref _commandInterval, value); }
    public string SerialMode { get => _serialMode; set => SetProperty(ref _serialMode, value); }
    public bool ShowRx { get => _showRx; set => SetProperty(ref _showRx, value); }
    public bool ShowTx { get => _showTx; set => SetProperty(ref _showTx, value); }
    public bool ShowTime { get => _showTime; set => SetProperty(ref _showTime, value); }
    public bool AnsiEnabled { get => _ansi; set => SetProperty(ref _ansi, value); }
    public bool AutoScroll { get => _autoScroll; set => SetProperty(ref _autoScroll, value); }
    // Interactive Industrial.Controls must keep their own buttons, checkboxes
    // and editors clickable while the canvas is in design mode. They expose a
    // small name/handle area for moving instead of making the whole control a
    // drag surface.
    public bool IsInput => Model.IsInput || ManualWidgetCatalog.Get(Model.Kind).Interactive;
    public bool IsDisplay => !IsInput;
    public bool HasCornerMoveHandle => ManualWidgetCatalog.Get(Model.Kind).HasCornerMoveHandle;
    public bool IsNumeric => Model.Kind is "numeric" or "readout" or "input" or "gauge" or "status" or "slider" or "range" or "progress" or "ring";
    public bool IsCommand => Model.Kind is "input" or "command" or "slider" or "toggle" or "mode" or "trigger";
    public bool IsSerial => Model.Kind == "serial";
    public bool IsGauge => Model.Kind == "gauge";
    public bool IsStatus => Model.Kind == "status";
    public GridBounds Bounds => _bounds;
    public double X => _bounds.Column * gridSize;
    public double Y => _bounds.Row * gridSize;
    public double Width => _bounds.Columns * gridSize;
    public double Height => _bounds.Rows * gridSize;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public void LoadStoredConfiguration()
    {
        if (string.IsNullOrWhiteSpace(Model.ConfigurationJson) || Model.ConfigurationJson == "{}") return;
        try
        {
            using var document = JsonDocument.Parse(Model.ConfigurationJson);
            var root = document.RootElement.TryGetProperty("Settings", out var settings) ? settings : document.RootElement;
            Name = Text(root, "Name", Name);
            Unit = Text(root, "Unit", Unit);
            Channel = Text(root, "Channel", Channel);
            Command = Text(root, "Command", Command);
            Precision = Number(root, "Precision", Precision);
            Minimum = Number(root, "Minimum", Minimum);
            Maximum = Number(root, "Maximum", Maximum);
            Step = Number(root, "Step", Step);
            WarningAbove = Number(root, "WarningAbove", WarningAbove);
            ErrorAbove = Number(root, "ErrorAbove", ErrorAbove);
            Encoding = Text(root, "Encoding", Encoding);
            Repeat = Number(root, "Repeat", Repeat);
            CommandInterval = Number(root, "IntervalMilliseconds", CommandInterval);
            if (document.RootElement.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty("PollIntervalMilliseconds", out var poll))
                PollInterval = poll.ToString();
            if (document.RootElement.TryGetProperty("Properties", out properties) && properties.ValueKind == JsonValueKind.Object)
            {
                var savedMode = Text(properties, "DisplayMode", SerialMode);
                SerialMode = savedMode.Equals("Hex", StringComparison.OrdinalIgnoreCase) ? "HEX" : savedMode.Equals("Abc", StringComparison.OrdinalIgnoreCase) ? "ABC" : savedMode;
                ShowRx = Bool(properties, "ShowRx", ShowRx); ShowTx = Bool(properties, "ShowTx", ShowTx);
                ShowTime = Bool(properties, "ShowTime", ShowTime); AnsiEnabled = Bool(properties, "AnsiEnabled", AnsiEnabled);
                AutoScroll = Bool(properties, "AutoScroll", AutoScroll);
            }
        }
        catch (JsonException) { }
    }
    public string MergeConfiguration(InstrumentSettings settings)
    {
        settings.Name = Name; settings.Unit = Unit; settings.Channel = Channel;
        settings.Command = Command; settings.Precision = ParseInt(Precision, settings.Precision);
        settings.Minimum = ParseDouble(Minimum, settings.Minimum); settings.Maximum = ParseDouble(Maximum, settings.Maximum);
        settings.Step = ParseDouble(Step, settings.Step); settings.WarningAbove = ParseDouble(WarningAbove, settings.WarningAbove);
        settings.ErrorAbove = ParseDouble(ErrorAbove, settings.ErrorAbove); settings.Encoding = Encoding;
        settings.Repeat = ParseInt(Repeat, settings.Repeat); settings.IntervalMilliseconds = ParseInt(CommandInterval, settings.IntervalMilliseconds);
        Dictionary<string, string>? properties = null;
        string? type = null;
        try
        {
            var stored = JsonSerializer.Deserialize<InstrumentFiles.ConfigurationFile>(Model.ConfigurationJson);
            properties = stored?.Properties; type = stored?.Type;
        }
        catch (JsonException) { }
        if (string.IsNullOrWhiteSpace(type))
        {
            using var defaults = ManualWidgetCatalog.Create(Model.Kind);
            type = defaults.GetType().Name;
        }
        properties ??= new(StringComparer.Ordinal);
        properties["PollIntervalMilliseconds"] = PollInterval;
        if (Model.Kind == "serial")
        {
            properties["DisplayMode"] = SerialMode.Equals("HEX", StringComparison.OrdinalIgnoreCase) ? "Hex" : "Abc";
            properties["ShowRx"] = ShowRx.ToString(); properties["ShowTx"] = ShowTx.ToString();
            properties["ShowTime"] = ShowTime.ToString(); properties["AnsiEnabled"] = AnsiEnabled.ToString();
            properties["AutoScroll"] = AutoScroll.ToString();
        }
        return JsonSerializer.Serialize(new InstrumentFiles.ConfigurationFile(1, type, settings, properties), InstrumentFiles.JsonOptions);
    }
    public void ReplaceModel(ManualWidgetDefinition model)
    {
        Model = model;
        Preview(model.Bounds);
        Name = model.Name; Unit = model.Unit;
        OnPropertyChanged(nameof(Model));
    }
    private static string Text(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static string Number(JsonElement root, string name, string fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.ToString() : fallback;
    private static bool Bool(JsonElement root, string name, bool fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static int ParseInt(string value, int fallback) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Max(0, parsed) : fallback;
    private static double ParseDouble(string value, double fallback) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) ? parsed : fallback;
    public void Preview(GridBounds bounds)
    {
        if (_bounds == bounds) return;
        var previous = _bounds;
        _bounds = bounds;
        // Notify only the geometry values that actually changed. An empty
        // property name makes WPF refresh every binding on the widget.
        if (previous.Column != bounds.Column) OnPropertyChanged(nameof(X));
        if (previous.Row != bounds.Row) OnPropertyChanged(nameof(Y));
        if (previous.Columns != bounds.Columns) OnPropertyChanged(nameof(Width));
        if (previous.Rows != bounds.Rows) OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(Bounds));
    }
    public ManualWidgetDefinition Capture() => Model with { Bounds = _bounds };
}

public sealed record ManualWidgetTool(string Kind, string Name, string Icon, string Category);

public sealed class ManualTestViewModel : ObservableObject, TestStation.App.Services.IProjectSaveParticipant
{
    private readonly IManualCanvasEditingService _editing;
    private readonly IManualCanvasDocumentService _documentService;
    private readonly IManualCanvasRepository _repository;
    private ManualCanvasDefinition _document = new();
    private string? _root;
    private string _documentId = "main";
    private string _propertyBaseline = "";
    public bool IsDirty => _dirty || HasPendingProperties;
    private string CaptureProperties() => Selected is null ? "" : JsonSerializer.Serialize(new {
        Selected.Name, Selected.Unit, Selected.Channel, Selected.Minimum, Selected.Maximum, Selected.Step, Selected.Precision,
        Selected.WarningAbove, Selected.ErrorAbove, Selected.Command, Selected.Encoding, Selected.Repeat, Selected.CommandInterval,
        Selected.SerialMode, Selected.ShowRx, Selected.ShowTx, Selected.ShowTime, Selected.AnsiEnabled, Selected.AutoScroll, Selected.PollInterval });
    public bool HasPendingProperties => Selected is not null && _propertyBaseline != CaptureProperties();
    private bool _editable = true, _loaded = true, _dirty;
    private double _viewportWidth = 800, _viewportHeight = 600;
    private string _status = UiText.Get("ManualCanvasHint");
    public event Action<ManualWidgetViewModel>? WidgetConfigurationChanged;
    public event Action? CaptureConfigurationsRequested;
    public ObservableCollection<ManualWidgetViewModel> Widgets { get; } = [];
    public ObservableCollection<ManualWidgetViewModel> SelectedWidgets { get; } = [];
    public IReadOnlyList<ManualWidgetTool> WidgetTools => ManualWidgetCatalog.Tools;
    public ManualWidgetViewModel? Selected { get; private set; }
    public ManualWidgetViewModel? LastInteracted { get; private set; }
    public int GridSize => _document.GridSize;
    public bool IsDesignMode
    {
        get => _editable;
        set { if (SetProperty(ref _editable, value)) { Select(null); NotifyCommands(); OnPropertyChanged(nameof(ModeCaption)); } }
    }
    public string ModeCaption => UiText.Get(IsDesignMode ? "CanvasSwitchToRun" : "CanvasSwitchToDesign");
    public string LibraryCaption => UiText.Get("CanvasWidgetLibrary");
    public string LibraryHint => UiText.Get("CanvasWidgetLibraryHint");
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public double SurfaceWidth => Math.Max(_viewportWidth, Widgets.Select(w => w.X + w.Width + 32).DefaultIfEmpty(0).Max());
    public double SurfaceHeight => Math.Max(_viewportHeight, Widgets.Select(w => w.Y + w.Height + 32).DefaultIfEmpty(0).Max());
    public RelayCommand AddWidgetCommand { get; }
    public RelayCommand DeleteWidgetCommand { get; }
    public RelayCommand ToggleModeCommand { get; }
    public RelayCommand ApplyWidgetPropertiesCommand { get; }
    public ManualTestViewModel(IManualCanvasEditingService editing, IManualCanvasRepository repository, IManualCanvasDocumentService documentService)
    {
        _editing = editing; _repository = repository; _documentService = documentService;
        AddWidgetCommand = new(AddWidget, () => IsDesignMode && _loaded && Widgets.Count < 500);
        DeleteWidgetCommand = new(DeleteWidget, () => IsDesignMode && _loaded && (SelectedWidgets.Count > 0 || Selected is not null || LastInteracted is not null));
        ToggleModeCommand = new(() => IsDesignMode = !IsDesignMode);
        ApplyWidgetPropertiesCommand = new(ApplyWidgetProperties, () => IsDesignMode && _loaded && Selected is not null);
    }
    public void OpenProject(string root, string documentId = "main")
    {
        if (_root == root && _documentId == documentId) return;
        _documentId = documentId;
        _root = root; _dirty = false; _loaded = true;
        try
        {
            var stored = _repository.Load(root, documentId);
            _document = stored ?? new() { Widgets = [NewWidget(0)] };
            if (_document.WidgetLibraryBaseline != "1.0.0")
            {
                if (stored is not null && _document.GridSize == 16)
                    foreach (var widget in _document.Widgets.ToArray())
                    {
                        var previousDefault = widget.Kind switch
                        {
                            "numeric" => (18, 4), "input" => (20, 4), "gauge" => (18, 12),
                            "status" => (18, 4), "command" => (14, 5), "serial" => (32, 15), _ => (0, 0)
                        };
                        if ((widget.Bounds.Columns, widget.Bounds.Rows) != previousDefault) continue;
                        var spec = ManualWidgetCatalog.Get(widget.Kind);
                        _documentService.UpdateBounds(_document, widget.Id, widget.Bounds with { Columns = spec.Columns, Rows = spec.Rows });
                    }
                _document.WidgetLibraryBaseline = "1.0.0";
                _dirty = stored is not null;
            }
            Rebuild(); Status = UiText.Get("ManualCanvasHint");
        }
        catch (Exception error)
        {
            _loaded = false; _document = new(); Rebuild(); Status = UiText.Format("CanvasLoadFailed", error.Message);
        }
        OnPropertyChanged(nameof(GridSize)); NotifyCommands();
    }
    public bool Flush()
    {
        if (!_dirty || _root is null) return true;
        try { _repository.Save(_root, _document, _documentId); _dirty = false; OnPropertyChanged(nameof(IsDirty)); return true; }
        catch (Exception error) { Status = UiText.Format("CanvasSaveFailed", error.Message); return false; }
    }
    public bool SavePendingProperties()
    {
        try { if (HasPendingProperties) ApplyWidgetProperties(); return Flush(); }
        catch (Exception error) { Status = error.Message; return false; }
    }
    public bool SaveDocument()
    {
        if (!_loaded || _root is null) return false;
        if (!SavePendingProperties()) return false;
        CaptureConfigurationsRequested?.Invoke();
        _dirty = true; OnPropertyChanged(nameof(IsDirty));
        return Flush();
    }
    public string SaveSection => "手动测试";
    public TestStation.App.Services.ProjectSaveResult SaveProjectChanges() => _root is null || SaveDocument()
        ? new(true) : new(false, Status);
    public void DiscardPendingProperties()
    {
        var root = _root; var id = _documentId;
        if (root is null) return;
        _root = null; OpenProject(root, id);
    }
    public void SetViewport(double width, double height)
    {
        _viewportWidth = Math.Max(1, width); _viewportHeight = Math.Max(1, height); NotifyExtent();
    }
    public void Select(ManualWidgetViewModel? widget)
    {
        if (widget is null) { ClearSelection(); return; }
        SetSelection([widget]);
    }

    public void SetSelection(IEnumerable<ManualWidgetViewModel> widgets, bool append = false)
    {
        if (!IsDesignMode) { ClearSelection(); return; }
        if (!append) ClearSelection();
        foreach (var widget in widgets.Where(Widgets.Contains).Distinct())
        {
            if (!SelectedWidgets.Contains(widget)) SelectedWidgets.Add(widget);
            widget.IsSelected = true;
            LastInteracted = widget;
        }
        Selected = SelectedWidgets.LastOrDefault();
        Selected?.LoadStoredConfiguration();
        _propertyBaseline = CaptureProperties();
        OnPropertyChanged(nameof(Selected));
        NotifyCommands();
    }

    public void ClearSelection()
    {
        foreach (var widget in SelectedWidgets) widget.IsSelected = false;
        SelectedWidgets.Clear();
        Selected = null;
        _propertyBaseline = "";
        OnPropertyChanged(nameof(Selected));
        NotifyCommands();
    }

    public void SelectInArea(double left, double top, double right, double bottom, bool append = false)
    {
        SetSelection(Widgets.Where(widget => widget.X >= left && widget.Y >= top &&
            widget.X + widget.Width <= right && widget.Y + widget.Height <= bottom).ToArray(), append);
    }

    public void ForgetInteraction()
    {
        LastInteracted = null;
        NotifyCommands();
    }
    public void RememberInteraction(ManualWidgetViewModel widget)
    {
        if (!IsDesignMode || !Widgets.Contains(widget)) return;
        LastInteracted = widget;
        NotifyCommands();
    }
    public GridBounds Preview(ManualWidgetViewModel widget, GridBounds origin, CanvasGesture gesture, double dx, double dy, bool refreshExtent = false, bool updateStatus = false)
    {
        if (!IsDesignMode || !_loaded) return origin;
        var bounds = CalculatePreview(origin, gesture, dx, dy, widget);
        widget.Preview(bounds);
        if (refreshExtent) NotifyExtent();
        if (updateStatus) Status = UiText.Format("CanvasBounds", bounds.Column, bounds.Row, bounds.Columns, bounds.Rows, GridSize);
        return bounds;
    }
    public GridBounds CalculatePreview(GridBounds origin, CanvasGesture gesture, double dx, double dy, ManualWidgetViewModel? widget = null)
    {
        var spec = widget is null ? null : ManualWidgetCatalog.Get(widget.Model.Kind);
        return _editing.Preview(origin, gesture, dx, dy, GridSize, spec?.MinimumColumns ?? 10, spec?.MinimumRows ?? 2);
    }
    public void CompleteGesture(ManualWidgetViewModel widget, GridBounds origin, bool cancel)
    {
        if (cancel) { widget.Preview(origin); NotifyExtent(); Status = UiText.Get("ManualCanvasHint"); return; }
        if (widget.Bounds == origin) return;
        if (_documentService.UpdateBounds(_document, widget.Id, widget.Bounds)) Changed();
    }
    public void Nudge(int x, int y)
    {
        if (!IsDesignMode || Selected is null) return;
        var origin = Selected.Bounds;
        Preview(Selected, origin, CanvasGesture.Move, x * GridSize, y * GridSize, refreshExtent: true, updateStatus: true);
        CompleteGesture(Selected, origin, false);
    }
    public void UpdateSample(double value)
    {
        foreach (var widget in Widgets) widget.Value = value.ToString("F3", CultureInfo.InvariantCulture);
    }
    public void AddWidget(string kind)
    {
        AddWidgetAt(kind, null, null);
    }

    public void AddWidgetAt(string kind, int? column, int? row)
    {
        if (!IsDesignMode || !_loaded || Widgets.Count >= 500) return;
        var definition = NewWidget(kind, Widgets.Count);
        if (column is not null || row is not null)
            definition = definition with
            {
                Bounds = definition.Bounds with
                {
                    Column = Math.Clamp(column ?? definition.Bounds.Column, 0, 10000 - definition.Bounds.Columns),
                    Row = Math.Clamp(row ?? definition.Bounds.Row, 0, 10000 - definition.Bounds.Rows)
                }
            };
        _documentService.AddWidget(_document, definition);
        var widget = new ManualWidgetViewModel(definition, GridSize);
        Widgets.Add(widget); Select(widget); NotifyExtent(); Changed();
    }
    public void StoreWidgetConfiguration(ManualWidgetViewModel widget, string configurationJson, bool persist = true)
    {
        if (!_loaded || string.IsNullOrWhiteSpace(configurationJson)) return;
        var index = _document.Widgets.FindIndex(item => item.Id == widget.Id);
        if (index < 0 || _document.Widgets[index].ConfigurationJson == configurationJson) return;
        var stored = JsonSerializer.Deserialize<InstrumentFiles.ConfigurationFile>(configurationJson);
        var updated = _document.Widgets[index] with
        {
            ConfigurationJson = configurationJson,
            Name = stored?.Settings.Name ?? widget.Name,
            Unit = stored?.Settings.Unit ?? widget.Unit
        };
        if (!_documentService.UpdateWidget(_document, updated)) return;
        widget.ReplaceModel(updated);
        _dirty = true; OnPropertyChanged(nameof(IsDirty));
        if (persist) Flush();
    }
    private void ApplyWidgetProperties()
    {
        if (Selected is null || !IsDesignMode || !_loaded) return;
        var current = Selected.Model;
        var settings = new InstrumentSettings();
        try
        {
            if (current.ConfigurationJson is { Length: > 2 })
                settings = JsonSerializer.Deserialize<InstrumentFiles.ConfigurationFile>(current.ConfigurationJson)?.Settings ?? settings;
        }
        catch (JsonException) { }
        var configuration = Selected.MergeConfiguration(settings);
        var index = _document.Widgets.FindIndex(item => item.Id == current.Id);
        if (index < 0) return;
        var updated = current with { Name = Selected.Name.Trim(), Unit = Selected.Unit.Trim(), ConfigurationJson = configuration };
        if (!_documentService.UpdateWidget(_document, updated)) return;
        Selected.ReplaceModel(updated);
        Changed();
        Status = UiText.Get("CanvasSaved");
        OnPropertyChanged(nameof(Selected));
        WidgetConfigurationChanged?.Invoke(Selected);
        _propertyBaseline = CaptureProperties();
    }
    public void DeleteWidget(ManualWidgetViewModel widget)
    {
        DeleteWidgets([widget]);
    }

    public void DeleteWidgets(IEnumerable<ManualWidgetViewModel> widgets)
    {
        if (!IsDesignMode || !_loaded) return;
        var targets = widgets.Where(Widgets.Contains).Distinct().ToArray();
        if (targets.Length == 0) return;
        _documentService.RemoveWidgets(_document, targets.Select(widget => widget.Id));
        foreach (var widget in targets)
        {
            Widgets.Remove(widget);
            if (ReferenceEquals(LastInteracted, widget)) LastInteracted = null;
        }
        ClearSelection();
        NotifyExtent(); NotifyCommands(); Changed();
    }
    private ManualWidgetDefinition NewWidget(int index) => NewWidget("numeric", index);
    private ManualWidgetDefinition NewWidget(string kind, int index)
    {
        var spec = ManualWidgetCatalog.Get(kind);
        var bounds = new GridBounds(3 + index % 5 * 3, 3 + index % 4 * 3, spec.Columns, spec.Rows);
        return new(Guid.NewGuid().ToString("N"), kind, spec.Name, spec.Unit, bounds, spec.Interactive);
    }
    private void AddWidget()
    {
        if (!AddWidgetCommand.CanExecute(null)) return;
        AddWidgetAt("numeric", null, null);
    }
    private void DeleteWidget()
    {
        var targets = SelectedWidgets.Count > 0
            ? SelectedWidgets.ToArray()
            : Selected is not null ? [Selected] : LastInteracted is not null ? [LastInteracted] : [];
        DeleteWidgets(targets);
    }
    private void Rebuild()
    {
        ClearSelection(); LastInteracted = null; Widgets.Clear(); foreach (var item in _document.Widgets) Widgets.Add(new(item, GridSize)); NotifyExtent(); NotifyCommands();
    }
    private void Changed() { _dirty = true; OnPropertyChanged(nameof(IsDirty)); NotifyCommands(); if (Flush()) Status = UiText.Get("CanvasSaved"); }
    private void NotifyCommands() { AddWidgetCommand.NotifyCanExecuteChanged(); DeleteWidgetCommand.NotifyCanExecuteChanged(); ApplyWidgetPropertiesCommand.NotifyCanExecuteChanged(); }
    private void NotifyExtent() { OnPropertyChanged(nameof(SurfaceWidth)); OnPropertyChanged(nameof(SurfaceHeight)); }
}
