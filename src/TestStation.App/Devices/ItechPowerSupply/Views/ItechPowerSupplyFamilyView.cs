using System.Windows;
using System.Windows.Controls;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
using TestStation.App.Services;

namespace TestStation.App.Devices.ItechPowerSupply.Views;

/// <summary>Composes operation and configuration views; neither ViewModel owns a View.</summary>
public sealed class ItechPowerSupplyFamilyView : UserControl, IDisposable
{
    private readonly ItechPowerSupplyView _operation;
    private readonly ItechPowerSupplyAdminView _admin;
    private readonly ItechPowerSupplyAdminViewModel _model;
    private readonly ContentControl _content = new();
    private bool _configuration;
    public ItechPowerSupplyFamilyView(ItechPowerSupplyView operation, ItechPowerSupplyAdminView admin,
        ItechPowerSupplyAdminViewModel model, ISequenceDialogService dialogs)
    {
        _operation = operation; _admin = admin; _model = model;
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 0) };
        foreach (var item in new[] { ("设备操作", false), ("家族配置", true) })
        {
            var button = new Button { Content = item.Item1, Margin = new Thickness(0, 0, 6, 0), MinWidth = 100 };
            button.SetResourceReference(StyleProperty, "AppButtonStyle");
            var configuration = item.Item2;
            button.Click += async (_, _) => { if (_configuration == configuration) return; if (_configuration && !await PrepareToLeaveAsync()) return; _configuration = configuration; _content.Content = configuration ? _admin : _operation; };
            navigation.Children.Add(button);
        }
        grid.Children.Add(navigation); Grid.SetRow(_content, 1); grid.Children.Add(_content); _content.Content = operation; Content = grid;
    }
    public Task<bool> PrepareToLeaveAsync() => !_admin.CommitEdits() ? Task.FromResult(false) : _model.PrepareToLeaveAsync();
    public void Dispose() { _operation.Dispose(); _model.Dispose(); }
}
