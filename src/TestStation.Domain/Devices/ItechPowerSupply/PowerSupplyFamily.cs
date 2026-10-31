using System.Text.Json;

namespace TestStation.Domain.Devices.ItechPowerSupply;

public sealed record PowerSupplyChannel
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public string DataType { get; set; }
    public string Unit { get; set; }
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public string ReadCommand { get; set; }
    public string? WriteCommand { get; set; }
    public PowerSupplyChannel(string id, string displayName, string dataType, string unit, bool canRead, bool canWrite, string readCommand, string? writeCommand = null)
    { Id = id; DisplayName = displayName; DataType = dataType; Unit = unit; CanRead = canRead; CanWrite = canWrite; ReadCommand = readCommand; WriteCommand = writeCommand; }
    public bool Enabled { get; set; } = true;
    public string Binding { get; set; } = "";
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public double Scale { get; set; } = 1;
    public double Offset { get; set; }
    public string TrueResponse { get; set; } = "1|ON";
    public string FalseResponse { get; set; } = "0|OFF";
    public string TrueCommand { get; set; } = "ON";
    public string FalseCommand { get; set; } = "OFF";
    public bool Poll { get; set; }
    public int IntervalMs { get; set; } = 500;
    public int TimeoutMs { get; set; } = 1500;
}

public sealed class PowerSupplyModel
{
    public string Id { get; set; } = "public-scpi";
    public string Name { get; set; } = "公共 SCPI 模拟配置";
    public bool Enabled { get; set; } = true;
    public double MaxVoltage { get; set; } = 60;
    public double MaxCurrent { get; set; } = 10;
    public double MaxPower { get; set; } = 600;
    public bool Voltage { get; set; } = true;
    public bool Current { get; set; } = true;
    public bool Output { get; set; } = true;
    public bool Protection { get; set; }
    public bool Bidirectional { get; set; }
    public bool Photovoltaic { get; set; }
    public int ChannelCount { get; set; } = 1;
    public bool Tcp { get; set; } = true;
    public bool Serial { get; set; } = true;
    public string ProtocolId { get; set; } = "scpi";
    public string DriverId { get; set; } = "itech.public-scpi";
    // Null means inherit the family definitions. A model override is one complete channel list.
    public List<PowerSupplyChannel>? Channels { get; set; }
}

public sealed class PowerSupplyFamily
{
    public int Version { get; set; } = 2;
    public string FamilyId { get; set; } = "itech.power-supply";
    public List<PowerSupplyChannel> Channels { get; set; } = [];
    public List<PowerSupplyModel> Models { get; set; } = [new()];
    public IReadOnlyList<PowerSupplyChannel> Resolve(PowerSupplyModel model) => model.Channels ?? Channels;
    public static PowerSupplyFamily Default() => new()
    {
        Models = ItechPowerSupplyModelCatalog.CreateModels(),
        Channels = [
            new("Voltage.Setpoint", "输出电压", "number", "V", true, true, "SOUR:VOLT?", "SOUR:VOLT {value}") { Binding = "voltage", Minimum = 0 },
            new("Current.Setpoint", "输出电流", "number", "A", true, true, "SOUR:CURR?", "SOUR:CURR {value}") { Binding = "current", Minimum = 0 },
            new("Voltage.Measurement", "测量电压", "number", "V", true, false, "MEAS:VOLT?") { Binding = "voltage_measurement", Poll = true },
            new("Current.Measurement", "测量电流", "number", "A", true, false, "MEAS:CURR?") { Binding = "current_measurement", Poll = true },
            new("Output.Enabled", "输出状态", "boolean", "", true, true, "OUTP?", "OUTP {value}") { Binding = "output" }
        ]
    };
    public PowerSupplyFamily Copy() => JsonSerializer.Deserialize<PowerSupplyFamily>(JsonSerializer.Serialize(this))!;
}
