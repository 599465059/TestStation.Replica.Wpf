using TestStation.Application.Drivers;
using TestStation.Domain.Devices;
using TestStation.Domain.Resources;

namespace TestStation.Application.Devices;

/// <summary>Shared instance lifecycle; protocol-specific drivers are supplied by family modules.</summary>
public interface IInstrumentDeviceService : IAutomationChannelBackend, ISequenceChannelChangeSource
{
    IReadOnlyList<InstrumentFamily> Definitions { get; }
    IReadOnlyList<IManagedDeviceFamily> Families { get; }
    IReadOnlyList<DeviceDefinition> Devices { get; }
    InstrumentModel ModelFor(string deviceId);
    ManagedDeviceState StateFor(string deviceId);
    event EventHandler? StateChanged;
    event EventHandler<InstrumentTraffic>? Traffic;
    Task SetFamilyEnabledAsync(string familyId, bool enabled, CancellationToken token = default);
}

public sealed record InstrumentTraffic(DateTimeOffset Time, string DeviceId, string Direction, string Message);
