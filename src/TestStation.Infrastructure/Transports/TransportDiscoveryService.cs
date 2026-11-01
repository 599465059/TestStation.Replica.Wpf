using System.IO.Ports;
using TestStation.Application.Hardware;
using TestStation.Infrastructure.Transports.Can;

namespace TestStation.Infrastructure.Transports;

public sealed class TransportDiscoveryService(IEnumerable<ICanVendorAdapter> adapters) : ITransportDiscoveryService
{
    private readonly ICanVendorAdapter[] _adapters = adapters.ToArray();
    public IReadOnlyList<string> SerialPorts() => SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<TransportBackendOption> CanBackends => CanAdapterCatalog.All.Select(a =>
        new TransportBackendOption(a.Id, a.DisplayName, _adapters.Any(v => v.BackendId == a.Id) && CanNativeLibraryLoader.GetStatus(a.Id).IsLoaded,
            _adapters.Any(v => v.BackendId == a.Id) ? CanNativeLibraryLoader.GetStatus(a.Id).Error ?? "" : "控制器会话适配器尚未接入"))
        .Prepend(new("simulated", "模拟 CAN 总线", true, "仅模拟通信，不使用真实硬件")).ToArray();
    public IReadOnlyList<TransportBackendOption> VisaBackends => [new("", "VISA 后端未安装", false, "当前版本尚未接入真实 VISA 运行库适配器")];
    public Task<IReadOnlyList<CanHardwareChannel>> CanChannelsAsync(string backendId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (backendId == "simulated") return Task.FromResult<IReadOnlyList<CanHardwareChannel>>([new("simulated", "CAN0", "模拟 CAN0", "", 0, true, true)]);
        var adapter = _adapters.FirstOrDefault(a => a.BackendId == backendId) ?? throw new NotSupportedException("所选 CAN 后端尚未接入");
        return Task.Run(adapter.Enumerate, token);
    }
}
