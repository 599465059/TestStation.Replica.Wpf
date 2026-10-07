using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Protocols;
using TestStation.Domain.Protocols;

namespace TestStation.App.ViewModels.Protocols;

public partial class ProtocolLibraryViewModel : ObservableObject
{
    private readonly IProtocolLibraryService _service;
    private ProtocolDefinition? _selectedProtocol;
    public ObservableCollection<ProtocolDefinition> Protocols { get; } = [];
    public Func<bool>? CanChangeSelection { get; set; }
    public ProtocolDefinition? SelectedProtocol
    {
        get => _selectedProtocol;
        set
        {
            if (_selectedProtocol?.Id != value?.Id && CanChangeSelection?.Invoke() == false)
            { OnPropertyChanged(nameof(SelectedProtocol)); return; }
            SetProperty(ref _selectedProtocol, value);
        }
    }
    [ObservableProperty] private string _status = "请先打开或创建工程";
    public ProtocolLibraryViewModel(IProtocolLibraryService service)
    {
        _service = service;
        service.Changed += (_, _) => Reload();
    }
    public void OpenProject(string root)
    {
        // Project transitions reset old selection before loading the new catalog.
        _selectedProtocol = null;
        _service.OpenProject(root);
    }
    private void Reload()
    {
        var id = SelectedProtocol?.Id;
        Protocols.Clear();
        foreach (var protocol in _service.Protocols.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)) Protocols.Add(protocol);
        SelectedProtocol = Protocols.FirstOrDefault(item => item.Id == id) ?? Protocols.FirstOrDefault();
        Status = _service.Errors.Count > 0 ? string.Join("; ", _service.Errors) : $"{Protocols.Count} 个协议";
    }
    [RelayCommand] private void NewProtocol() => Try(() =>
    {
        if (CanChangeSelection?.Invoke() == false) return;
        var protocol = new ProtocolDefinition { Id = "protocol_" + Guid.NewGuid().ToString("N")[..8], Name = $"自定义协议 {Protocols.Count + 1}",
            Frames = [new ProtocolFrame { Id = "frame_1", Name = "请求帧" }] };
        _service.Save(null, protocol);
        SelectedProtocol = Protocols.Single(item => item.Id == protocol.Id);
    });
    [RelayCommand] private void DeleteProtocol() => Try(() =>
    {
        if (SelectedProtocol is null || CanChangeSelection?.Invoke() == false) return;
        _service.Remove(SelectedProtocol.Id);
    });
    public void Import(string path) => Try(() =>
    {
        if (CanChangeSelection?.Invoke() == false) return;
        var protocol = _service.Import(path);
        SelectedProtocol = Protocols.Single(item => item.Id == protocol.Id);
    });
    public void Export(string path) => Try(() =>
    {
        if (SelectedProtocol is null) throw new ArgumentException("请选择协议");
        _service.Export(SelectedProtocol.Id, path);
        Status = "协议已导出";
    });
    public void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or System.Text.Json.JsonException or OverflowException or FormatException or UnauthorizedAccessException)
        { Status = error.Message; }
    }
}
