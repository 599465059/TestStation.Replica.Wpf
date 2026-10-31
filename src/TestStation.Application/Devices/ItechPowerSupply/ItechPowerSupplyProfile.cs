using System.Text.Json;
using TestStation.Application.Drivers;

namespace TestStation.Application.Devices.ItechPowerSupply;

/// <summary>Model-level capability and command data. Vendor-specific profiles can replace this default.</summary>
public sealed record ItechPowerSupplyProfile(
    string FamilyId,
    string ModelId,
    string Name,
    double MaxVoltage,
    double MaxCurrent,
    IReadOnlyList<PowerSupplyChannel> Channels)
{
    public static ItechPowerSupplyProfile PublicScpi { get; } = new(
        "itech.power-supply", "public-scpi", "公共 SCPI 电源", 60, 10,
        [
            new(ScpiPowerSupplyChannels.VoltageSetpoint, "输出电压", "number", "V", true, true, "SOUR:VOLT?", "SOUR:VOLT {value}"),
            new(ScpiPowerSupplyChannels.CurrentSetpoint, "输出电流", "number", "A", true, true, "SOUR:CURR?", "SOUR:CURR {value}"),
            new(ScpiPowerSupplyChannels.VoltageMeasurement, "测量电压", "number", "V", true, false, "MEAS:VOLT?"),
            new(ScpiPowerSupplyChannels.CurrentMeasurement, "测量电流", "number", "A", true, false, "MEAS:CURR?"),
            new(ScpiPowerSupplyChannels.OutputEnabled, "输出状态", "boolean", "", true, true, "OUTP?", "OUTP {value}")
        ]);

    public PowerSupplyChannel Channel(string id) => Channels.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"电源通道不存在：{id}");
}

public interface IItechPowerSupplyProfileStore
{
    ItechPowerSupplyProfile Current { get; }
    PowerSupplyFamily Family { get; }
    void SaveFamily(PowerSupplyFamily family);
    event EventHandler? Changed;
    void Save(ItechPowerSupplyProfile profile);
    void Reset();
}

