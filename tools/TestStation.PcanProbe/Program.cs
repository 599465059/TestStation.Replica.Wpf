using TestStation.Infrastructure.Transports.Can.Pcan;
using TestStation.Infrastructure.Transports.Can;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Transports;

try
{
    var adapter = new PcanVendorAdapter();
    if (args.Contains("--all", StringComparer.OrdinalIgnoreCase))
    {
        foreach (var (descriptor, status) in CanAdapterCatalog.GetStatuses())
            Console.WriteLine($"{descriptor.Id}: {descriptor.LoadMode} / {status.ResolvedPath ?? "未找到"} / {status.Error ?? "可用"}");
        return 0;
    }
    var native = adapter.NativeLibrary;
    Console.WriteLine($"PCAN-Basic 加载方式：{native.LoadMode}，路径：{native.ResolvedPath ?? "未找到"}");
    var channels = adapter.Enumerate();
    Console.WriteLine($"PCAN-Basic 通道数：{channels.Count}");
    foreach (var channel in channels)
        Console.WriteLine($"{channel.ChannelId}  {channel.Name}  Device={channel.DeviceId}  Controller={channel.ControllerIndex}  FD={channel.SupportsFd}  Available={channel.Available}");
    if (args.Contains("--loopback", StringComparer.OrdinalIgnoreCase))
    {
        var resource1 = Resource("pcan-probe-1", "PCAN_USBBUS1");
        var resource2 = Resource("pcan-probe-2", "PCAN_USBBUS2");
        var factory = new TransportSessionFactory(canAdapters: [adapter]);
        await using var sender = factory.CreateCan(resource1);
        await using var receiver = factory.CreateCan(resource2);
        await sender.OpenAsync(new(resource1.Id, resource1.Type, resource1.Settings, ResourceLeaseMode.SharedBus));
        await receiver.OpenAsync(new(resource2.Id, resource2.Type, resource2.Settings, ResourceLeaseMode.SharedBus));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var receive = receiver.ReceiveAsync(new CanFrameFilter(Id: 0x5A5), timeout.Token).FirstAsync(timeout.Token).AsTask();
        var sent = new CanFrame(0x5A5, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        await sender.SendAsync(sent, timeout.Token);
        var received = await receive;
        Console.WriteLine($"真实收发成功：ID=0x{received.Id:X3} DATA={Convert.ToHexString(received.Data.ToArray())}");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

static TransportResource Resource(string id, string channel)
{
    var resource = new TransportResource { Id = id, Name = id, Type = "can", LeaseMode = "shared_bus" };
    TransportSettingsAccessor.Write(resource, new(CanChannel: channel, CanBitrate: 500000, CanBackendId: "pcan"));
    return resource;
}
