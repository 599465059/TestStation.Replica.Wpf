using System.Text.Json;

namespace TestStation.Application.Drivers;

/// <summary>Device I/O behind the shared manual/automatic access coordinator.</summary>
public interface IChannelIoBackend : IAsyncDisposable
{
    IReadOnlyList<string> ChannelIds { get; }
    Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default);
    Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Optional device backend capabilities used by the shared manual/automatic coordinator.</summary>
public interface IAutomationChannelBackend : IChannelIoBackend, ISequenceChannelProvider
{
    bool IsAutomationActive { get; set; }
    bool OwnsChannel(string channelId);
    Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default);
    Task EndAutomationAsync(CancellationToken cancellationToken = default);
}

public sealed record ScpiEndpoint(string Host = "127.0.0.1", int Port = 5025, int TimeoutMs = 1500);
public sealed record ScpiConnectionInfo(ScpiEndpoint Endpoint, bool Connected = false, string Identity = "", string Error = "");

public interface IScpiPowerSupplyConnection
{
    ScpiConnectionInfo Connection { get; }
    event EventHandler? ConnectionChanged;
    Task ConnectAsync(ScpiEndpoint endpoint, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Channel IDs are retained for compatibility with existing sequences.</summary>
public static class ScpiPowerSupplyChannels
{
    public const string VoltageSetpoint = "ITECH01.Voltage.Setpoint";
    public const string CurrentSetpoint = "ITECH01.Current.Setpoint";
    public const string VoltageMeasurement = "ITECH01.Voltage.Measurement";
    public const string CurrentMeasurement = "ITECH01.Current.Measurement";
    public const string OutputEnabled = "ITECH01.Output.Enabled";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { VoltageSetpoint, CurrentSetpoint, VoltageMeasurement, CurrentMeasurement, OutputEnabled });
}

/// <summary>Only these channels are routed through the parent process device service.</summary>
public interface IExternalChannelRuntime
{
    IReadOnlyList<string> ExternalChannelIds { get; }
    IReadOnlyDictionary<string, string> ExternalChannelTypes => new Dictionary<string, string>();
    bool IsExternalChannel(string id) => ExternalChannelIds.Contains(id, StringComparer.OrdinalIgnoreCase);
}
