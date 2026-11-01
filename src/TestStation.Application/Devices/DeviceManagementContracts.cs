using TestStation.Domain.Resources;

namespace TestStation.Application.Devices;

public sealed record ManagedDeviceModel(string Id, string Name, IReadOnlyList<string> TransportTypes);
public sealed record ManagedDeviceState(bool Connected, string Error = "");

/// <summary>家族模块发布管理能力，管理页面不识别厂商或创建驱动。</summary>
public interface IManagedDeviceFamily
{
    string Id { get; }
    string Name { get; }
    bool Enabled { get; }
    IReadOnlyList<ManagedDeviceModel> Models { get; }
    event EventHandler? Changed;
    ManagedDeviceState GetState(string deviceId);
    DeviceDefinition Add(string name);
    void Update(string id, string name, string modelId, string resourceId);
    void Remove(string id);
    Task ConnectAsync(string id, CancellationToken token = default);
    Task DisconnectAsync(string id, CancellationToken token = default);
    Task DisconnectAllAsync(CancellationToken token = default);
}

public interface IDeviceManagementService
{
    IReadOnlyList<IManagedDeviceFamily> Families { get; }
    IManagedDeviceFamily Resolve(string familyId);
    Task DisconnectAllAsync(CancellationToken token = default);
}

public sealed class DeviceManagementService(IEnumerable<IManagedDeviceFamily> families) : IDeviceManagementService
{
    public IReadOnlyList<IManagedDeviceFamily> Families { get; } = families.ToArray();
    public IManagedDeviceFamily Resolve(string familyId) => Families.FirstOrDefault(f => f.Id.Equals(familyId, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"设备家族未接入：{familyId}");
    public async Task DisconnectAllAsync(CancellationToken token = default)
    { foreach (var family in Families) await family.DisconnectAllAsync(token).ConfigureAwait(false); }
}
