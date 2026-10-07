using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application;
using TestStation.App.Localization;
using TestStation.App.Services;

namespace TestStation.App.ViewModels;

public sealed partial class ManualWorkspaceTab : ObservableObject
{
    public ManualWorkspaceTab(string id, string name, ManualTestViewModel model)
    { Id = id; _name = name; Model = model; Model.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(ManualTestViewModel.IsDirty) or nameof(ManualTestViewModel.Status)) OnPropertyChanged(nameof(Title)); }; }
    public string Id { get; }
    public ManualTestViewModel Model { get; }
    [ObservableProperty] private string _name;
    public string Title => Name + (Model.IsDirty ? " *" : "");
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Title));
}

public sealed partial class ManualWorkspaceViewModel : ObservableObject, IProjectSaveParticipant
{
    private readonly IManualCanvasEditingService _editing;
    private readonly IManualCanvasRepository _canvasRepository;
    private readonly IManualCanvasDocumentService _documentService;
    private readonly IManualWorkspaceRepository _workspaces;
    private readonly IManualWorkspaceDialogService _dialogs;
    private List<ManualWorkspaceEntry> _catalog = [];
    private string? _projectRoot;
    public ObservableCollection<ManualWorkspaceTab> Documents { get; } = [];
    public ObservableCollection<ManualWorkspaceEntry> ClosedDocuments { get; } = [];
    [ObservableProperty] private ManualWorkspaceTab? _activeDocument;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    private bool _isDesignMode = true;
    public bool IsDesignMode
    {
        get => _isDesignMode;
        set
        {
            if (_isDesignMode == value) return;
            _isDesignMode = value;
            foreach (var document in Documents) document.Model.IsDesignMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeCaption));
        }
    }
    public string ModeCaption => UiText.Get(IsDesignMode ? "CanvasSwitchToRun" : "CanvasSwitchToDesign");
    public bool HasProject => _projectRoot is not null;

    public ManualWorkspaceViewModel(IManualCanvasEditingService editing, IManualCanvasRepository canvasRepository,
        IManualCanvasDocumentService documentService, IManualWorkspaceRepository workspaces, IManualWorkspaceDialogService dialogs)
    { _editing = editing; _canvasRepository = canvasRepository; _documentService = documentService; _workspaces = workspaces; _dialogs = dialogs; }

    public void OpenProject(string root)
    {
        if (string.Equals(_projectRoot, root, StringComparison.OrdinalIgnoreCase)) return;
        var entries = _workspaces.Load(root).ToList();
        if (entries.Count == 0) entries.Add(new ManualWorkspaceEntry("main", "未命名"));
        if (entries.All(item => !item.IsOpen)) entries[0] = entries[0] with { IsOpen = true };
        _projectRoot = root; _catalog = entries; Documents.Clear();
        foreach (var entry in entries.Where(item => item.IsOpen)) Documents.Add(CreateTab(entry));
        RefreshClosed(); ActiveDocument = Documents.FirstOrDefault(); Status = "";
        OnPropertyChanged(nameof(HasProject));
    }

    [RelayCommand] private void NewDocument()
    {
        try { AddDocument(); } catch (Exception error) { Status = error.Message; }
    }
    public ManualWorkspaceTab AddDocument()
    {
        if (_projectRoot is null) throw new InvalidOperationException(UiText.Get("ManualOpenProjectFirst"));
        var number = 1;
        while (_catalog.Any(item => item.Name == UiText.Format("ManualWorkspaceDefaultName", number))) number++;
        var entry = new ManualWorkspaceEntry("manual_" + Guid.NewGuid().ToString("N"), UiText.Format("ManualWorkspaceDefaultName", number));
        var tab = CreateTab(entry);
        if (!tab.Model.SaveDocument()) throw new InvalidOperationException(tab.Model.Status);
        Persist(_catalog.Append(entry).ToList());
        Documents.Add(tab); ActiveDocument = tab; return tab;
    }

    [RelayCommand] private async Task RenameDocument(ManualWorkspaceTab? tab)
    {
        tab ??= ActiveDocument;
        if (tab is null || IsBusy) return;
        IsBusy = true;
        try { var name = await _dialogs.RenameAsync(tab.Name); if (name is not null) Rename(tab, name); }
        catch (Exception error) { Status = error.Message; }
        finally { IsBusy = false; }
    }
    public void Rename(ManualWorkspaceTab tab, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 100) throw new InvalidDataException(UiText.Get("ManualWorkspaceInvalidName"));
        Persist(_catalog.Select(item => item.Id == tab.Id ? item with { Name = name } : item).ToList());
        tab.Name = name;
    }
    [RelayCommand] public async Task CloseDocument(ManualWorkspaceTab? tab)
    {
        if (tab is null || IsBusy) return;
        if (Documents.Count <= 1) { Status = "至少需要保留一个手动测试工区"; return; }
        IsBusy = true;
        try
        {
            if (!await ResolvePendingAsync(tab)) return;
            Persist(_catalog.Select(item => item.Id == tab.Id ? item with { IsOpen = false } : item).ToList());
            var index = Documents.IndexOf(tab); Documents.Remove(tab);
            if (ActiveDocument == tab) ActiveDocument = Documents.ElementAtOrDefault(Math.Max(0, index - 1));
            RefreshClosed();
        }
        catch (Exception error) { Status = error.Message; }
        finally { IsBusy = false; }
    }
    [RelayCommand] private void ReopenDocument(ManualWorkspaceEntry? entry)
    {
        if (entry is null || Documents.Any(item => item.Id == entry.Id)) return;
        try
        {
            var tab = CreateTab(entry);
            Persist(_catalog.Select(item => item.Id == entry.Id ? item with { IsOpen = true } : item).ToList());
            Documents.Add(tab); ActiveDocument = tab; RefreshClosed();
        }
        catch (Exception error) { Status = error.Message; }
    }
    public string SaveSection => "手动测试";
    public ProjectSaveResult SaveProjectChanges()
    {
        if (_projectRoot is null) return new(true);
        foreach (var tab in Documents)
            if (!tab.Model.SaveDocument()) return new(false, $"{tab.Name}：{tab.Model.Status}");
        Persist(_catalog);
        Status = UiText.Get("ManualWorkspaceSaved");
        return new(true);
    }
    public async Task<bool> PrepareToLeaveAsync()
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            foreach (var tab in Documents) if (!await ResolvePendingAsync(tab)) return false;
            return Flush();
        }
        catch (Exception error) { Status = error.Message; return false; }
        finally { IsBusy = false; }
    }

    public bool Flush()
    {
        if (_projectRoot is null) return true;
        if (Documents.Any(item => !item.Model.Flush())) return false;
        Persist(_catalog);
        return true;
    }

    private async Task<bool> ResolvePendingAsync(ManualWorkspaceTab tab)
    {
        if (!tab.Model.IsDirty) return true;
        var choice = await _dialogs.AskUnsavedAsync(tab.Name);
        if (choice == SequenceUnsavedChoice.Cancel) return false;
        if (choice == SequenceUnsavedChoice.Discard) { tab.Model.DiscardPendingProperties(); return true; }
        if (tab.Model.SaveDocument()) return true;
        Status = tab.Model.Status; return false;
    }
    private ManualWorkspaceTab CreateTab(ManualWorkspaceEntry entry)
    {
        var model = new ManualTestViewModel(_editing, _canvasRepository, _documentService);
        model.OpenProject(_projectRoot!, entry.Id);
        model.IsDesignMode = IsDesignMode;
        return new(entry.Id, entry.Name, model);
    }

    [RelayCommand]
    private void ToggleMode() => IsDesignMode = !IsDesignMode;
    private void Persist(List<ManualWorkspaceEntry> entries)
    {
        _workspaces.Save(_projectRoot!, entries); _catalog = entries;
    }
    private void RefreshClosed()
    {
        ClosedDocuments.Clear(); foreach (var entry in _catalog.Where(item => !item.IsOpen)) ClosedDocuments.Add(entry);
    }
}
