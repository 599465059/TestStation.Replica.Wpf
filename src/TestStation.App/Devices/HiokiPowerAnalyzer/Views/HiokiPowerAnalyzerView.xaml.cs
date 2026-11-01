using System.Windows;
using System.Windows.Controls;
using TestStation.App.Devices.HiokiPowerAnalyzer.ViewModels;

namespace TestStation.App.Devices.HiokiPowerAnalyzer.Views;

public partial class HiokiPowerAnalyzerView : UserControl, IDisposable, TestStation.App.Services.IDevicePanelSelection
{
    private readonly HiokiPowerAnalyzerViewModel _model;
    public HiokiPowerAnalyzerView(HiokiPowerAnalyzerViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        IsVisibleChanged += (_, _) => { if (IsVisible) _model.StartPolling(); else _model.StopPolling(); };
        Unloaded += (_, _) => _model.StopPolling();
    }
    public void SelectDevice(string deviceId) => _model.SelectedDevice = _model.Devices.FirstOrDefault(d => d.Id == deviceId);
    public void Dispose() => _model.Dispose();
}
