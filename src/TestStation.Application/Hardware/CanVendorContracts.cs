namespace TestStation.Application.Hardware;

/// <summary>Vendor-neutral CAN adapter port implemented only by Infrastructure.</summary>
public interface ICanVendorAdapter
{
    string BackendId { get; }
    string NormalizeChannel(string channel);
    IReadOnlyList<CanHardwareChannel> Enumerate();
    ICanController Open(CanBusOptions options);
}

public sealed record CanBusOptions(string BackendId, string Channel, int BitRate,
    bool FdEnabled, int DataBitRate, bool ListenOnly);

public interface ICanController : IDisposable
{
    void Send(CanFrame frame);
    bool TryReceive(out CanFrame? frame);
}
