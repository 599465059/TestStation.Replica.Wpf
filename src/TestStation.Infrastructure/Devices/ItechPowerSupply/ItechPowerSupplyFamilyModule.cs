using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Scpi;

namespace TestStation.Infrastructure.Devices.ItechPowerSupply;

/// <summary>Factory boundary used by the future external family module.</summary>
public sealed class ItechPowerSupplyDriverFactory : IDeviceDriverFactory
{
    private readonly ITransportSessionFactory _transports;
    private readonly IItechPowerSupplyProfileStore _profiles;
    public ItechPowerSupplyDriverFactory(ITransportSessionFactory transports, IItechPowerSupplyProfileStore profiles)
    { _transports = transports; _profiles = profiles; }

    public IDeviceDriver Create(DeviceDefinition device, TransportResource resource)
        => Build(device, resource).Driver;

    public ItechPowerSupplyDeviceSession CreateSession(DeviceDefinition device, TransportResource resource, Action<ScpiProtocolSession.TrafficRecord>? traffic = null)
    {
        var built = Build(device, resource);
        if (traffic is not null) built.Protocol.Traffic += (_, record) => traffic(record);
        return new ItechPowerSupplyDeviceSession(device.Id, resource.Id, built.Protocol, built.Driver);
    }

    private (ScpiProtocolSession Protocol, ItechPowerSupplyDriver Driver) Build(DeviceDefinition device, TransportResource resource)
    {
        var model = _profiles.Family.Models.FirstOrDefault(m => m.Id.Equals(device.ModelId, StringComparison.OrdinalIgnoreCase) && m.Enabled)
            ?? throw new InvalidOperationException($"型号不可用：{device.ModelId}");
        var type = TransportSettingsAccessor.NormalizeType(resource.Type);
        if (type is not ("tcp" or "serial")) throw new NotSupportedException("ITECH 电源仅支持 TCP 和串口");
        TransportSettingsAccessor.ValidateEndpoint(resource);
        var transport = _transports.CreateByte(resource);
        var endpoint = new TransportEndpoint(resource.Id, type, resource.Settings);
        var protocol = new ScpiProtocolSession(transport, endpoint);
        var channels = _profiles.Family.Resolve(model).Where(c => c.Enabled).Select(c => c with { Id = device.Id + "." + c.Id }).ToArray();
        var profile = new ItechPowerSupplyProfile(_profiles.Family.FamilyId, model.Id, model.Name, model.MaxVoltage, model.MaxCurrent, channels);
        return (protocol, new ItechPowerSupplyDriver(device.Id, protocol, profile));
    }
}

/// <summary>WPF-free descriptor that can later be exported as an external plugin module.</summary>
public sealed class ItechPowerSupplyFamilyModule : IDeviceFamilyModule
{
    private readonly IItechPowerSupplyProfileStore _profiles;
    public ItechPowerSupplyFamilyModule(IItechPowerSupplyProfileStore profiles, ItechPowerSupplyDriverFactory driverFactory)
    { _profiles = profiles; DriverFactory = driverFactory; }
    public DeviceFamilyDefinition Definition { get; } = new(
        "itech.power-supply", "艾德克斯 IT6000 电源", "ITECH", "1.0", true, true, false, false);
    public IReadOnlyList<DeviceModelDefinition> Models => _profiles.Family.Models.Select(model =>
        new DeviceModelDefinition(model.Id, model.Name, _profiles.Family.Resolve(model).Select(channel => channel.Id).ToArray())).ToArray();
    public IDeviceDriverFactory DriverFactory { get; }
}
