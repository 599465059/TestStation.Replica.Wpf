using System.Text.Json;
using TestStation.Application;

namespace TestStation.Infrastructure;

/// <summary>
/// JSON file under %LOCALAPPDATA%\TestStation. Unreadable files fall back to defaults (and are left
/// untouched until the next successful save) so a damaged file never blocks startup.
/// </summary>
public sealed class StationSettingsStore : IStationSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly object _sync = new();
    private StationSettings _current;

    public StationSettingsStore() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "settings.json")) { }

    public StationSettingsStore(string path)
    {
        Location = path;
        _current = Load(path);
    }

    public string Location { get; }
    public StationSettings Current { get { lock (_sync) return _current; } }
    public event EventHandler? Changed;

    public void Update(Func<StationSettings, StationSettings> change)
    {
        StationSettings next;
        lock (_sync)
        {
            next = change(_current).Normalize();
            if (next == _current) return;
            Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
            var temporary = Location + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(next, Options));
            File.Move(temporary, Location, true);
            _current = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static StationSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? (JsonSerializer.Deserialize<StationSettings>(File.ReadAllText(path), Options) ?? new()).Normalize()
                : new StationSettings();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StationSettings();
        }
    }
}
