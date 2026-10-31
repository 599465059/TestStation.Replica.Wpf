using System.Text.Json;
using TestStation.Application.Drivers;
using TestStation.Application.Hardware;

namespace TestStation.Application.Devices.ItechPowerSupply;

public sealed class ItechPowerSupplyService(IDeviceSession session) : IItechPowerSupplyService
{
    public Task ConnectAsync(CancellationToken cancellationToken = default) => session.ConnectAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => session.DisconnectAsync(cancellationToken);
    public Task SetVoltageAsync(double value, CancellationToken cancellationToken = default) => session.WriteAsync(ScpiPowerSupplyChannels.VoltageSetpoint, JsonSerializer.SerializeToElement(value), cancellationToken);
    public Task SetCurrentAsync(double value, CancellationToken cancellationToken = default) => session.WriteAsync(ScpiPowerSupplyChannels.CurrentSetpoint, JsonSerializer.SerializeToElement(value), cancellationToken);
    public Task SetOutputAsync(bool enabled, CancellationToken cancellationToken = default) => session.WriteAsync(ScpiPowerSupplyChannels.OutputEnabled, JsonSerializer.SerializeToElement(enabled), cancellationToken);
    public async Task<PowerSupplyReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var voltage = await session.ReadAsync(ScpiPowerSupplyChannels.VoltageMeasurement, cancellationToken).ConfigureAwait(false);
        var current = await session.ReadAsync(ScpiPowerSupplyChannels.CurrentMeasurement, cancellationToken).ConfigureAwait(false);
        var output = await session.ReadAsync(ScpiPowerSupplyChannels.OutputEnabled, cancellationToken).ConfigureAwait(false);
        return new(voltage.GetDouble(), current.GetDouble(), output.GetBoolean());
    }
}
