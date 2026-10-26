using System.Text.Json.Serialization;
using System.ComponentModel;

namespace TestStation.Domain.Can;

public sealed record CanSignalValue(string Name, string Unit, string Value, DateTimeOffset Timestamp);

public sealed record CanMessageSnapshot(string Id, string Name, int Dlc, string Data, DateTimeOffset Timestamp);

public sealed class CanCanvasWidget : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "signal_monitor";
    public string Title { get; set; } = "CAN 信号监视";
    private int _left, _top, _width = 460, _height = 250;
    public int Left { get => _left; set => Set(ref _left, value, nameof(Left)); }
    public int Top { get => _top; set => Set(ref _top, value, nameof(Top)); }
    public int Width { get => _width; set => Set(ref _width, value, nameof(Width)); }
    public int Height { get => _height; set => Set(ref _height, value, nameof(Height)); }
    public bool Enabled { get; set; } = true;
    private bool _isSelected;
    [JsonIgnore] public bool IsSelected { get => _isSelected; set { if (_isSelected == value) return; _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set(ref int field, int value, string name) { if (field == value) return; field = value; PropertyChanged?.Invoke(this, new(name)); }
}

public sealed class CanTestWorkspace
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "未命名 CAN 测试";
    public string? DbcPath { get; set; }
    public string? ChannelId { get; set; }
    public List<CanCanvasWidget> Widgets { get; set; } = [];
}
