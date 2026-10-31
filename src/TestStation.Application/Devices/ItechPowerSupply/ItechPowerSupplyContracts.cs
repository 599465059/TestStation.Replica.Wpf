using System.Text.Json;

namespace TestStation.Application.Devices.ItechPowerSupply;

public sealed record PowerSupplyReading(double Voltage, double Current, bool OutputEnabled);
public interface IItechPowerSupplyService
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SetVoltageAsync(double value, CancellationToken cancellationToken = default);
    Task SetCurrentAsync(double value, CancellationToken cancellationToken = default);
    Task SetOutputAsync(bool enabled, CancellationToken cancellationToken = default);
    Task<PowerSupplyReading> ReadAsync(CancellationToken cancellationToken = default);
}
