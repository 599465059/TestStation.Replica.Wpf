using System.Text.Json;
using TestStation.Application.Devices.ItechPowerSupply;

namespace TestStation.Infrastructure.Devices.ItechPowerSupply;

public sealed class ItechPowerSupplyProfileStore : IItechPowerSupplyProfileStore
{
    private readonly string _path;
    private PowerSupplyFamily _family;
    public ItechPowerSupplyProfileStore(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "device-profiles", "itech-power-supply.json");
        _family = PowerSupplyFamily.Default();
        if (!File.Exists(_path)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(_path));
        if (json.RootElement.TryGetProperty("Version", out _)) _family = json.Deserialize<PowerSupplyFamily>()!;
        else
        {
            var legacy = json.Deserialize<ItechPowerSupplyProfile>() ?? throw new InvalidDataException("旧电源配置无法读取");
            var defaults = PowerSupplyFamily.Default();
            _family.Channels = legacy.Channels.Select(c => c with { Id = LocalId(c.Id), Binding = defaults.Channels.FirstOrDefault(d => d.Id == LocalId(c.Id))?.Binding ?? "", Poll = LocalId(c.Id).EndsWith("Measurement") }).ToList();
            _family.Models[0].MaxVoltage = legacy.MaxVoltage; _family.Models[0].MaxCurrent = legacy.MaxCurrent;
        }
        PowerSupplyValidation.Validate(_family);
    }
    public static string LocalId(string id) => id.StartsWith("ITECH01.", StringComparison.OrdinalIgnoreCase) ? id[8..] : id;
    public PowerSupplyFamily Family => _family.Copy();
    public ItechPowerSupplyProfile Current => new(_family.FamilyId, _family.Models[0].Id, _family.Models[0].Name,
        _family.Models[0].MaxVoltage, _family.Models[0].MaxCurrent, _family.Resolve(_family.Models[0]).Select(c => c with { Id = "ITECH01." + c.Id }).ToArray());
    public event EventHandler? Changed;
    public void Save(ItechPowerSupplyProfile profile)
    { var family = Family; family.Channels = profile.Channels.Select(c => c with { Id = LocalId(c.Id) }).ToList(); SaveFamily(family); }
    public void SaveFamily(PowerSupplyFamily family)
    {
        PowerSupplyValidation.Validate(family);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(family, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, _path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        _family = family.Copy(); Changed?.Invoke(this, EventArgs.Empty);
    }
    public void Reset() => SaveFamily(PowerSupplyFamily.Default());
}
