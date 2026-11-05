using System.Text.Json;
using TestStation.Domain.Devices;

namespace TestStation.Infrastructure.Devices.Reference;

// Snapshot DTOs contain protocol data, never old device lifetimes, pages or polling loops.
internal sealed class ScpiFamilyData
{
    public string FamilyId { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public List<ScpiModelData> Models { get; set; } = [];
}
internal sealed class ScpiModelData
{
    public string Id { get; set; } = "";
    public ScpiCommandSet Definition { get; set; } = new();
}
internal sealed class ScpiCommandSet
{
    public string Terminator { get; set; } = "\n";
    public bool Upper { get; set; }
    public int DelayMs { get; set; }
    public int TimeoutMs { get; set; } = 3000;
    public int ChannelCount { get; set; } = 1;
    public string ChannelSelect { get; set; } = "";
    public string MeasurementQuery { get; set; } = "";
    public int MeasurementTimeoutMs { get; set; }
    public List<string> MeasurementParameters { get; set; } = [];
    public List<string> Connect { get; set; } = [];
    public List<string> Disconnect { get; set; } = [];
    public List<ScpiAction> Commands { get; set; } = [];
    public List<ScpiParameter> Parameters { get; set; } = [];
}
internal sealed class ScpiParameter
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? QueryCommand { get; set; }
    public string? WriteCommand { get; set; }
    public string DataType { get; set; } = "String";
    public string? Unit { get; set; }
    public string? ResponseRegex { get; set; }
    public string? ResponseGroup { get; set; }
    public double ResponseScale { get; set; } = 1;
    public double ResponseOffset { get; set; }
    public Dictionary<string, JsonElement> ResponseMap { get; set; } = [];
    public int Channel { get; set; } = 1;
    public bool IsReadOnly { get; set; }
    public bool IsMeasured { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3000;
    public int DelayAfterMilliseconds { get; set; }
    public string? Description { get; set; }
    public string Type => DataType switch { "Double" => "number", "Integer" => "integer", "Boolean" => "boolean", "String" => "string", _ => throw new InvalidDataException($"不支持的 SCPI 数据类型：{DataType}") };
}
internal sealed class ScpiAction
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CommandText { get; set; } = "";
    public string CommandType { get; set; } = "Command";
    public string? Description { get; set; }
    public int DelayAfterMilliseconds { get; set; }
    public int TimeoutMilliseconds { get; set; } = 3000;
    public bool IsEnabled { get; set; } = true;
}

internal sealed record InstrumentRegistration(InstrumentModel Model,
    Func<string, TestStation.Domain.Resources.TransportResource, Action<string, string>, InstrumentConnection> Create);

/// <summary>Protocol/session owner for an individual device. No polling or resource acquisition.</summary>
internal abstract class InstrumentConnection : IAsyncDisposable
{
    public abstract Task OpenAsync(CancellationToken token);
    public abstract Task<JsonElement> ReadAsync(string localChannel, CancellationToken token);
    public abstract Task WriteAsync(string localChannel, JsonElement value, CancellationToken token);
    public abstract ValueTask DisposeAsync();
}
