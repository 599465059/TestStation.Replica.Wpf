using System.Windows;
using System.Windows.Controls;
using TestStation.App.Devices.HiokiPowerAnalyzer.ViewModels;

namespace TestStation.App.Devices.HiokiPowerAnalyzer.Views;

public partial class HiokiPowerAnalyzerView : UserControl, IDisposable
{
    private readonly HiokiPowerAnalyzerViewModel _model;
    public HiokiPowerAnalyzerView(HiokiPowerAnalyzerViewModel model)
    {
        InitializeComponent(); _model = model; DataContext = model;
        IsVisibleChanged += (_, _) => { if (IsVisible) _model.StartPolling(); else _model.StopPolling(); };
        Unloaded += (_, _) => _model.StopPolling();
    }
    public void Dispose() => _model.Dispose();
}
