namespace TestStation.Infrastructure.Transports.Can;

/// <summary>Installed CAN backends known by the application.</summary>
public sealed record CanAdapterDescriptor(
    string Id,
    string DisplayName,
    string LoadMode,
    string PrimaryLibrary,
    bool SupportsClassicCan,
    bool SupportsCanFd,
    string AvailabilityNote);

public static class CanAdapterCatalog
{
    public static IReadOnlyList<CanAdapterDescriptor> All { get; } =
    [
        new("pcan", "PEAK PCAN-Basic", "bundled", "PCANBasic.dll", true, true, "已接入 PCAN-Basic 会话；已通过真实硬件验证"),
        new("zlg", "ZLG CAN", "bundled", "zlgcan.dll", true, true, "已随软件发布 native DLL；控制器会话适配器按型号配置"),
        new("kvaser", "Kvaser CANlib", "bundled", "canlib32.dll", true, true, "已随软件发布 native DLL；需要 Kvaser 硬件验证"),
        new("vector", "Vector XL-API", "bundled", "vxlapi64.dll", true, true, "已随软件发布 native DLL；需要 Vector 驱动和硬件验证"),
        new("tosun", "同星 TOSUN", "bundled", "libTSCAN.dll", true, true, "已随软件发布 TSCAN/TSH DLL；需要同星硬件验证"),
        new("ni-xnet", "NI-XNET", "installed", "nixnet.dll", true, true, "需要安装 NI-XNET Runtime，软件按系统规则查找"),
        new("etas", "ETAS BOA", "installed", "dll-ocdProxy.dll", true, true, "需要安装 ETAS BOA，软件按注册表和安装目录查找")
    ];

    public static CanAdapterDescriptor? Find(string id) => All.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<(CanAdapterDescriptor Adapter, CanNativeLibraryStatus Status)> GetStatuses() =>
        All.Select(adapter => (adapter, CanNativeLibraryLoader.GetStatus(adapter.Id))).ToArray();
}
