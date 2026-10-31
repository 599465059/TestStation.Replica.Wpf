using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices;

/// <summary>WPF-free metadata published by an installed device family.</summary>
public sealed record DeviceFamilyDefinition(
    string FamilyId,
    string Name,
    string Vendor,
    string Version,
    bool SupportsTcp,
    bool SupportsSerial,
    bool SupportsVisa,
    bool SupportsCan);

/// <summary>Catalog boundary used by the future external plugin loader.</summary>
public interface IDeviceFamilyCatalog
{
    IReadOnlyList<DeviceFamilyDefinition> Families { get; }
    bool IsEnabled(string familyId);
}

/// <summary>Small in-process catalog; external discovery can replace it later.</summary>
public sealed class DeviceFamilyCatalog : IDeviceFamilyCatalog
{
    private readonly Dictionary<string, (DeviceFamilyDefinition Definition, bool Enabled)> _families = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    public IReadOnlyList<DeviceFamilyDefinition> Families
    {
        get { lock (_sync) return _families.Values.Select(item => item.Definition).ToArray(); }
    }
    public bool IsEnabled(string familyId)
    { lock (_sync) return _families.TryGetValue(familyId, out var item) && item.Enabled; }
    public void Register(DeviceFamilyDefinition definition, bool enabled = true)
    { ArgumentException.ThrowIfNullOrWhiteSpace(definition.FamilyId); lock (_sync) _families[definition.FamilyId] = (definition, enabled); }
    public void SetEnabled(string familyId, bool enabled)
    { lock (_sync) if (_families.TryGetValue(familyId, out var item)) _families[familyId] = (item.Definition, enabled); }
}

/// <summary>Application module boundary. Implementations must not create WPF controls.</summary>
public interface IDeviceFamilyModule
{
    DeviceFamilyDefinition Definition { get; }
    IReadOnlyList<DeviceModelDefinition> Models { get; }
    IDeviceDriverFactory DriverFactory { get; }
}

/// <summary>Generic instance management boundary shared by device pages and sequence access.</summary>
public interface IDeviceInstanceService
{
    IReadOnlyList<DeviceDefinition> Devices { get; }
    IReadOnlyList<TransportResource> Resources { get; }
    DeviceDefinition AddDevice(string name, string familyId, string modelId);
    void UpdateDevice(string id, string name, string modelId, string resourceId);
    void RemoveDevice(string id);
    Task<IDeviceSession> ConnectAsync(string deviceId, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default);
}
