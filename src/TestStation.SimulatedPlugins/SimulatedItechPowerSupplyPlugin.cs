using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TestStation.Application;
using TestStation.Application.Drivers;
using System.Globalization;

namespace TestStation.SimulatedPlugins;

public sealed class SimulatedItechPowerSupplyPlugin : ISimulatedDevicePlugin
{
    public string Id => "simulated.itech.power-supply";
    public string Name => DeviceText.Get("ScpiPowerSupply");
    public string Version => "0.2.0";

    public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
    [
        new(ScpiPowerSupplyChannels.VoltageSetpoint, "number", Name, CanRead: true, CanWrite: true),
        new(ScpiPowerSupplyChannels.CurrentSetpoint, "number", Name, CanRead: true, CanWrite: true),
        new(ScpiPowerSupplyChannels.VoltageMeasurement, "number", Name, CanRead: true, CanWrite: false),
        new(ScpiPowerSupplyChannels.CurrentMeasurement, "number", Name, CanRead: true, CanWrite: false),
        new(ScpiPowerSupplyChannels.OutputEnabled, "boolean", Name, CanRead: true, CanWrite: true)
    ];

    public IReadOnlyDictionary<string, JsonElement> DefaultValues { get; } = new Dictionary<string, JsonElement>();

    public FrameworkElement CreateManualView(string workspaceId, string workspaceName, IChannelRuntimeCoordinator runtime, IDeviceModelCatalog? models = null) =>
        new ItechPowerSupplyView(runtime, models ?? new SimulatedDeviceModelCatalog());

    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => DefaultValues;
}

internal sealed class ItechPowerSupplyView : SimulatedDeviceView
{
    private readonly TextBox _voltage;
    private readonly TextBox _current;
    private readonly TextBlock _voltageMeasurement;
    private readonly TextBlock _currentMeasurement;
    private readonly CheckBox _output;
    private readonly IScpiPowerSupplyConnection? _connection;
    private readonly TextBox _host;
    private readonly TextBox _port;
    private readonly TextBlock _connectionStatus;
    private bool _viewDisposed;

    public ItechPowerSupplyView(IChannelRuntimeCoordinator runtime, IDeviceModelCatalog models) : base(runtime, models, "simulated.itech.power-supply")
    {
        _connection = runtime as IScpiPowerSupplyConnection;
        var endpoint = _connection?.Connection.Endpoint ?? new ScpiEndpoint();
        var connectionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _host = NumberBox(endpoint.Host); _host.Width = 150; _host.MinWidth = 100;
        _port = NumberBox(endpoint.Port.ToString(CultureInfo.InvariantCulture)); _port.MinWidth = 65; _port.Width = 65;
        _host.Margin = new Thickness(0, 0, 6, 0); _port.Margin = new Thickness(0, 0, 8, 0);
        System.Windows.Automation.AutomationProperties.SetName(_host, DeviceText.Get("ScpiHost"));
        System.Windows.Automation.AutomationProperties.SetName(_port, DeviceText.Get("ScpiPort"));
        connectionRow.Children.Add(_host); connectionRow.Children.Add(_port);
        connectionRow.Children.Add(Button(DeviceText.Get("Connect"), Connect));
        connectionRow.Children.Add(Button(DeviceText.Get("Disconnect"), async (_, _) =>
        { if (_connection is not null) await _connection.DisconnectAsync(); }));
        TrackOperation(_host); TrackOperation(_port); Body.Children.Add(connectionRow);
        _connectionStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 500, Margin = new Thickness(0, 0, 0, 10) };
        Body.Children.Add(_connectionStatus);
        if (_connection is not null) _connection.ConnectionChanged += ConnectionChanged;
        UpdateConnection();
        var groups = new WrapPanel(); Body.Children.Add(groups);
        var settings = Section("输出设定"); groups.Children.Add(settings);
        var measurements = Section("测量值"); groups.Children.Add(measurements);
        var voltage = FormGrid();
        Put(voltage, new TextBlock { Text = "电压 (V)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltage = NumberBox("12.0"); Put(voltage, _voltage, 1); settings.Children.Add(voltage);
        var current = FormGrid();
        Put(current, new TextBlock { Text = "电流 (A)", VerticalAlignment = VerticalAlignment.Center }, 0);
        _current = NumberBox("1.5"); Put(current, _current, 1); settings.Children.Add(current);
        ForChannel(current, "ITECH01.Current.Setpoint");
        _output = new CheckBox { Content = "输出开启", Margin = new Thickness(0, 6, 0, 0) };
        _output.SetResourceReference(Control.ForegroundProperty, "TextBrush"); settings.Children.Add(_output);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(Button("应用设定", Apply, true));
        actions.Children.Add(Button("读取一次", Read)); settings.Children.Add(actions);
        var readings = FormGrid();
        Put(readings, new TextBlock { Text = "电压", VerticalAlignment = VerticalAlignment.Center }, 0);
        _voltageMeasurement = Value(); Put(readings, _voltageMeasurement, 1);
        measurements.Children.Add(readings);
        var currentReading = FormGrid();
        Put(currentReading, new TextBlock { Text = "电流", VerticalAlignment = VerticalAlignment.Center }, 0);
        _currentMeasurement = Value(); Put(currentReading, _currentMeasurement, 1);
        measurements.Children.Add(currentReading);
        ForChannel(currentReading, "ITECH01.Current.Measurement");
        OnPolled("ITECH01.Voltage.Measurement", value => _voltageMeasurement.Text = $"{value.Value.GetDouble():0.###} V");
        OnPolled("ITECH01.Current.Measurement", value => _currentMeasurement.Text = $"{value.Value.GetDouble():0.###} A");
    }

    private void ConnectionChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(UpdateConnection);

    private void UpdateConnection()
    {
        if (_viewDisposed) return;
        var info = _connection?.Connection;
        _connectionStatus.Text = info?.Connected == true ? info.Identity : DeviceText.Get("Disconnected");
        _connectionStatus.SetResourceReference(TextBlock.ForegroundProperty, info?.Connected == true ? "SuccessBrush" : "MutedBrush");
    }

    private async Task Connect(object? sender, RoutedEventArgs e)
    {
        if (_connection is null) throw new InvalidOperationException(DeviceText.Get("ScpiUnavailable"));
        if (!int.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException(DeviceText.Get("ScpiPortInvalid"));
        await _connection.ConnectAsync(new ScpiEndpoint(_host.Text.Trim(), port));
        await Read(sender, e);
    }

    public override void Dispose()
    {
        _viewDisposed = true;
        if (_connection is not null) _connection.ConnectionChanged -= ConnectionChanged;
        base.Dispose();
    }

    private async Task Apply(object? sender, RoutedEventArgs e)
    {
        if (!TryNumber(_voltage, out var voltage) || voltage < 0)
        { SetStatus("电压和电流必须是不小于 0 的数值。", true); return; }
        var current = 0.0;
        if (Supports("ITECH01.Current.Setpoint") && (!TryNumber(_current, out current) || current < 0))
        { SetStatus("电流必须是不小于 0 的数值。", true); return; }
        await Runtime.WriteAsync("ITECH01.Voltage.Setpoint", JsonSerializer.SerializeToElement(voltage), "manual");
        if (Supports("ITECH01.Current.Setpoint")) await Runtime.WriteAsync("ITECH01.Current.Setpoint", JsonSerializer.SerializeToElement(current), "manual");
        await Runtime.WriteAsync("ITECH01.Output.Enabled", JsonSerializer.SerializeToElement(_output.IsChecked == true), "manual");
        SetStatus("设定已应用。");
    }

    private async Task Read(object? sender, RoutedEventArgs e)
    {
        var setVoltage = await Runtime.ReadOnceAsync("ITECH01.Voltage.Setpoint", "manual");
        _voltage.Text = setVoltage.Value.GetDouble().ToString("G", CultureInfo.InvariantCulture);
        if (Supports("ITECH01.Current.Setpoint"))
        {
            var setCurrent = await Runtime.ReadOnceAsync("ITECH01.Current.Setpoint", "manual");
            _current.Text = setCurrent.Value.GetDouble().ToString("G", CultureInfo.InvariantCulture);
        }
        var output = await Runtime.ReadOnceAsync("ITECH01.Output.Enabled", "manual");
        _output.IsChecked = output.Value.GetBoolean();
        var measuredVoltage = await Runtime.ReadOnceAsync("ITECH01.Voltage.Measurement", "manual");
        _voltageMeasurement.Text = $"{measuredVoltage.Value.GetDouble():0.###} V";
        if (Supports("ITECH01.Current.Measurement"))
        {
            var measuredCurrent = await Runtime.ReadOnceAsync("ITECH01.Current.Measurement", "manual");
            _currentMeasurement.Text = $"{measuredCurrent.Value.GetDouble():0.###} A";
        }
        SetStatus("");
    }
}
