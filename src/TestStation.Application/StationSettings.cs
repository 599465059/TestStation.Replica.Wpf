namespace TestStation.Application;

/// <summary>
/// Settings for this computer (all projects). Only settings with real behavior live here;
/// project-level settings belong to the project files.
/// </summary>
public sealed record StationSettings
{
    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; init; } = "Dark";
    /// <summary>"Blue", "Purple", "Teal" or "Orange".</summary>
    public string Accent { get; init; } = "Blue";
    public bool NavigationExpanded { get; init; } = true;
    /// <summary>Turn device outputs off before disconnecting (manual disconnect, project switch, exit, end of run).</summary>
    public bool TurnOutputOffBeforeDisconnect { get; init; } = true;
    /// <summary>How long Stop waits for the execution host (including CLEANUP) before killing it.</summary>
    public int StopWaitSeconds { get; init; } = 15;

    public static readonly int[] StopWaitChoices = [5, 15, 30, 60];

    /// <summary>Clamps values read from disk so a hand-edited file cannot break startup.</summary>
    public StationSettings Normalize() => this with
    {
        Theme = Theme is "Light" ? "Light" : "Dark",
        Accent = Accent is "Purple" or "Teal" or "Orange" ? Accent : "Blue",
        StopWaitSeconds = Math.Clamp(StopWaitSeconds, 5, 300)
    };
}

public interface IStationSettings
{
    StationSettings Current { get; }
    /// <summary>Raised after a successful save.</summary>
    event EventHandler? Changed;
    void Update(Func<StationSettings, StationSettings> change);
    /// <summary>File the settings are stored in (shown on the settings page).</summary>
    string Location { get; }
}
