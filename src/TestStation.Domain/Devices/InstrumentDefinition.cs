namespace TestStation.Domain.Devices;

/// <summary>Protocol-independent device capabilities. Protocol encodings live in Infrastructure.</summary>
public sealed record InstrumentChannel(string Id, string Name, string DataType, string Unit,
    bool CanRead, bool CanWrite, bool IsMeasurement = false, double? Minimum = null,
    double? Maximum = null, bool IsAction = false, string Description = "");

public sealed record InstrumentModel(string Id, string Name, string Series, string Protocol,
    IReadOnlyList<string> Transports, IReadOnlyList<InstrumentChannel> Channels,
    string Source, string Validation = "未完成实机验证", bool RuntimeAvailable = true);

public sealed record InstrumentFamily(string Id, string Name, string Category,
    IReadOnlyList<InstrumentModel> Models);
