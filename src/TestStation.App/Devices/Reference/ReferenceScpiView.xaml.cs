using System.Windows;
using System.Windows.Controls;
namespace TestStation.App.Devices.Reference;
public partial class ReferenceScpiView : UserControl, IDisposable
{
    private readonly ReferenceScpiViewModel _model;
    public ReferenceScpiView(ReferenceScpiViewModel model)
    {
        _model = model;
        InitializeComponent(); DataContext = model;
        Loaded += (_, _) => UpdateVisibility();
        Unloaded += (_, _) => model.StopPolling();
        IsVisibleChanged += (_, _) => UpdateVisibility();
        void UpdateVisibility() { if (IsLoaded && IsVisible) model.StartPolling(); else model.StopPolling(); }
    }
    public void Dispose() => _model.Dispose();
}
