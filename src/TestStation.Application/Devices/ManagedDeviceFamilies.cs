using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices;

public sealed class PowerSupplyManagementAdapter(IPowerSupplyDeviceService devices, IItechPowerSupplyProfileStore profiles) : IManagedDeviceFamily
{
    public string Id => "itech.power-supply";
    public string Name => "艾德克斯直流电源";
    public bool Enabled => devices.FamilyEnabled;
    public IReadOnlyList<ManagedDeviceModel> Models => profiles.Family.Models.Where(m => m.Enabled)
        .Select(m => new ManagedDeviceModel(m.Id, m.Name, (m.Tcp ? new[] { "tcp" } : []).Concat(m.Serial ? ["serial"] : []).ToArray())).ToArray();
    public event EventHandler? Changed { add { devices.Changed += value; devices.StateChanged += value; } remove { devices.Changed -= value; devices.StateChanged -= value; } }
    public ManagedDeviceState GetState(string id) { var state = devices.ConnectionFor(id); return new(state.Connected, state.Error, state.Connected ? state.OutputEnabled : null); }
    public DeviceDefinition Add(string name) => devices.AddDevice(name);
    public void Update(string id, string name, string modelId, string resourceId) => devices.UpdateDevice(id, name, modelId, resourceId);
    public void Remove(string id) => devices.RemoveDevice(id);
    public Task ConnectAsync(string id, CancellationToken token = default) => devices.ConnectAsync(id, token);
    public Task DisconnectAsync(string id, CancellationToken token = default) => devices.DisconnectDeviceAsync(id, token);
    public Task DisconnectAllAsync(CancellationToken token = default) => devices.DisconnectAsync(token);
}

public sealed class HiokiManagementAdapter(IHiokiPowerAnalyzerDeviceService devices) : IManagedDeviceFamily
{
    public string Id => HiokiPowerAnalyzerCatalog.FamilyId;
    public string Name => "HIOKI PW6001 功率分析仪";
    public bool Enabled => devices.FamilyEnabled;
    public IReadOnlyList<ManagedDeviceModel> Models => [new("PW6001", "HIOKI PW6001", ["tcp"])];
    public event EventHandler? Changed { add { devices.Changed += value; devices.StateChanged += value; } remove { devices.Changed -= value; devices.StateChanged -= value; } }
    public ManagedDeviceState GetState(string id) { var state = devices.ConnectionFor(id); return new(state.Connected, state.Error); }
    public DeviceDefinition Add(string name) => devices.AddDevice(name);
    public void Update(string id, string name, string modelId, string resourceId) => devices.UpdateDevice(id, name, modelId, resourceId);
    public void Remove(string id) => devices.RemoveDevice(id);
    public Task ConnectAsync(string id, CancellationToken token = default) => devices.ConnectAsync(id, token);
    public Task DisconnectAsync(string id, CancellationToken token = default) => devices.DisconnectDeviceAsync(id, token);
    public Task DisconnectAllAsync(CancellationToken token = default) => devices.DisconnectAsync(token);
}
