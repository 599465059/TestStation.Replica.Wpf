using TestStation.Application;

namespace TestStation.SimulatedPlugins;

/// <summary>Demonstration profiles, not specifications for real instrument models.</summary>
public sealed class SimulatedDeviceModelCatalog : IDeviceModelCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, IReadOnlyList<DeviceModelDefinition>> _families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["simulated.itech.power-supply"] =
        [
            new("psu-standard", DeviceText.Get("PsuStandard"), ["ITECH01.Voltage.Setpoint", "ITECH01.Current.Setpoint", "ITECH01.Voltage.Measurement", "ITECH01.Current.Measurement", "ITECH01.Output.Enabled"])
        ],
        ["simulated.electronic-load"] =
        [
            new("load-standard", DeviceText.Get("LoadStandard"), ["LOAD01.Current.Setpoint", "LOAD01.Voltage.Measurement", "LOAD01.Current.Measurement", "LOAD01.Input.Enabled"]),
            new("load-current", DeviceText.Get("LoadCurrent"), ["LOAD01.Current.Setpoint", "LOAD01.Current.Measurement", "LOAD01.Input.Enabled"])
        ],
        ["simulated.digital-multimeter"] =
        [
            new("dmm-standard", DeviceText.Get("DmmStandard"), ["DMM01.Voltage.Measurement", "DMM01.Current.Measurement", "DMM01.Continuity"]),
            new("dmm-voltage", DeviceText.Get("DmmVoltage"), ["DMM01.Voltage.Measurement"])
        ]
    };
    private readonly Dictionary<string, DeviceModelDefinition> _selected = new(StringComparer.OrdinalIgnoreCase);
    public Func<bool>? IsBusy { get; set; }
    public event EventHandler? Changed;

    public IReadOnlyList<DeviceModelDefinition> GetModels(string familyId) => _families.TryGetValue(familyId, out var models)
        ? models : throw new KeyNotFoundException(DeviceText.Format("FamilyMissing", familyId));

    public DeviceModelDefinition GetSelected(string familyId)
    {
        lock (_sync) return _selected.GetValueOrDefault(familyId) ?? GetModels(familyId)[0];
    }

    public void Select(string familyId, string modelId)
    {
        if (IsBusy?.Invoke() == true) throw new InvalidOperationException(DeviceText.Get("ModelChangeBusy"));
        var model = GetModels(familyId).FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(DeviceText.Format("ModelMissing", modelId), nameof(modelId));
        lock (_sync)
        {
            if (GetSelected(familyId).Id == model.Id) return;
            _selected[familyId] = model;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
