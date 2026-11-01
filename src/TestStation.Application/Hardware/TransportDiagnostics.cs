using TestStation.Domain.Resources;

namespace TestStation.Application.Hardware;

public sealed record TransportBackendOption(string Id, string Name, bool Available, string Note);
public interface ITransportDiscoveryService
{
    IReadOnlyList<string> SerialPorts();
    IReadOnlyList<TransportBackendOption> CanBackends { get; }
    IReadOnlyList<TransportBackendOption> VisaBackends { get; }
    Task<IReadOnlyList<CanHardwareChannel>> CanChannelsAsync(string backendId, CancellationToken token = default);
}

public interface ITransportDiagnosticService : IAsyncDisposable
{
    bool IsOpen { get; }
    string ResourceId { get; }
    bool IsCan { get; }
    event EventHandler<string>? Traffic;
    Task OpenAsync(string resourceId, CancellationToken token = default);
    Task CloseAsync();
    Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> bytes, bool response, TimeSpan timeout, CancellationToken token = default);
    Task SendCanAsync(CanFrame frame, CancellationToken token = default);
    Task<CanFrame> ReadCanAsync(CanFrameFilter filter, TimeSpan timeout, CancellationToken token = default);
}
