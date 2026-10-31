using System.Windows;
using System.Windows.Controls;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
namespace TestStation.App.Devices.ItechPowerSupply.Views;
public partial class ItechPowerSupplyView : UserControl, IDisposable
{
    private readonly ItechPowerSupplyViewModel _model;
    public ItechPowerSupplyView(ItechPowerSupplyViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        IsVisibleChanged += (_, _) => { if (IsVisible) _model.StartPolling(); else _model.StopPolling(); };
        Unloaded += (_, _) => _model.StopPolling();
    }
    public void Dispose() => _model.Dispose();
}
