using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Transports;

static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(name);
        Console.WriteLine("PASS " + name);
    }

    public static async Task Main()
    {
        await VerifyTcpAsync();
        VerifySettings();
        await VerifyCanAsync();
        await VerifyVisaAsync();
        Console.WriteLine($"RESULT=PASS {_checks} checks");
    }

    private static async Task VerifyTcpAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            await using var stream = socket.GetStream();
            var buffer = new byte[32];
            var count = await stream.ReadAsync(buffer);
            Check(Encoding.ASCII.GetString(buffer, 0, count) == "PING\n", "TCP 请求真实字节到达回环服务");
            await stream.WriteAsync(Encoding.ASCII.GetBytes("PONG\n"));
        });
        var resource = new TransportResource { Id = "tcp_probe", Name = "TCP probe", Type = "tcp" };
        TransportSettingsAccessor.Write(resource, new(Host: "127.0.0.1", TcpPort: port, ConnectTimeoutMs: 1000));
        var session = ((ITransportSessionFactory)new TransportSessionFactory()).CreateByte(resource);
        await using (session)
        {
            await session.OpenAsync(new(resource.Id, resource.Type, resource.Settings));
            var response = await session.ExchangeAsync(Encoding.ASCII.GetBytes("PING\n"), true, TimeSpan.FromSeconds(1));
            Check(Encoding.ASCII.GetString(response!.Value.Span) == "PONG", "TCP 字节请求响应和终止符处理");
            await session.CloseAsync();
            Check(session.State == TestStation.Application.Drivers.TransportState.Closed, "TCP 关闭释放状态");
        }
        await server;
        listener.Stop();
    }

    private static void VerifySettings()
    {
        var serial = new TransportResource { Id = "serial_probe", Name = "串口 probe", Type = "serial" };
        TransportSettingsAccessor.Write(serial, new(Port: "COM1", BaudRate: 115200, DataBits: 8, Parity: "None", StopBits: "1", SerialMode: "RS485", FlowControl: "None", HalfDuplex: true, DirectionDelayMs: 2));
        TransportSettingsAccessor.ValidateEndpoint(serial);
        Check(TransportSettingsAccessor.Read(serial).SerialMode == "RS485", "RS232/RS422/RS485 共用串口配置模型");
        serial.Settings["mode"] = JsonSerializer.SerializeToElement("INVALID");
        try { TransportSettingsAccessor.ValidateEndpoint(serial); throw new InvalidOperationException("串口非法模式未拒绝"); }
        catch (InvalidDataException) { Check(true, "串口非法模式被拒绝"); }
    }

    private static async Task VerifyCanAsync()
    {
        var first = new SimulatedCanBusSession();
        var second = new SimulatedCanBusSession();
        await using (first) await using (second)
        {
            var endpoint = new TransportEndpoint("can_probe", "can", new Dictionary<string, JsonElement>
            {
                ["channel"] = JsonSerializer.SerializeToElement("CAN0")
            }, ResourceLeaseMode.SharedBus);
            await first.OpenAsync(endpoint);
            await second.OpenAsync(endpoint);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var receive = second.ReceiveAsync(new CanFrameFilter(Id: 0x120, Mask: 0x7F0, DataPrefix: [0xAA]), cancellation.Token).FirstAsync(cancellation.Token).AsTask();
            await first.SendAsync(new CanFrame(0x123, new byte[] { 0xAA, 0x55 }, Timestamp: DateTimeOffset.UtcNow));
            var frame = await receive;
            Check(frame.Id == 0x123 && frame.Data.Span.SequenceEqual(new byte[] { 0xAA, 0x55 }), "CAN 模拟总线共享、过滤和持续接收");

            // 发送和订阅可以分开发生：会话级历史缓存应让后订阅者读到已经到达的帧。
            await first.SendAsync(new CanFrame(0x124, new byte[] { 0x10, 0x20 }));
            using var lateCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var lateFrame = await second.ReceiveAsync(new CanFrameFilter(Id: 0x124), lateCancellation.Token)
                .FirstAsync(lateCancellation.Token);
            Check(lateFrame.Data.Span.SequenceEqual(new byte[] { 0x10, 0x20 }), "CAN 先发送后订阅仍可读取历史帧");

            // 请求响应在发送前登记匹配器；响应方返回得再快也不会丢失。
            using var responderCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var responder = Task.Run(async () =>
            {
                await foreach (var request in second.ReceiveAsync(new CanFrameFilter(Id: 0x200), responderCancellation.Token))
                {
                    await second.SendAsync(new CanFrame(0x201, new byte[] { 0x42 }));
                    break;
                }
            });
            var response = await first.SendAndReceiveAsync(
                new CanFrame(0x200, new byte[] { 0x01 }), new CanFrameFilter(Id: 0x201),
                TimeSpan.FromSeconds(1), responderCancellation.Token);
            Check(response.Id == 0x201 && response.Data.Span.SequenceEqual(new byte[] { 0x42 }), "CAN 请求响应先登记匹配再发送");
            responderCancellation.Cancel();
            try { await responder; } catch (OperationCanceledException) { }

            // 同一帧广播给独立订阅者，读取一个订阅者不会消费另一个订阅者的帧。
            using var fanoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var firstSubscriber = first.ReceiveAsync(new CanFrameFilter(Id: 0x300), fanoutCancellation.Token).FirstAsync(fanoutCancellation.Token).AsTask();
            var secondSubscriber = second.ReceiveAsync(new CanFrameFilter(Id: 0x300), fanoutCancellation.Token).FirstAsync(fanoutCancellation.Token).AsTask();
            await Task.Delay(20, fanoutCancellation.Token);
            await first.SendAsync(new CanFrame(0x300, new byte[] { 0x33 }));
            var fanout = await Task.WhenAll(firstSubscriber, secondSubscriber);
            Check(fanout.All(item => item.Id == 0x300 && item.Data.Span.SequenceEqual(new byte[] { 0x33 })), "CAN 多订阅者各自收到同一帧");
            await first.CloseAsync();
            Check(first.State == TestStation.Application.Drivers.TransportState.Closed, "CAN 关闭释放会话");
        }
        var root = Path.Combine(Path.GetTempPath(), "teststation-transport-" + Guid.NewGuid().ToString("N"));
        var catalog = new ResourceCatalogService(new TestStation.Infrastructure.Resources.ResourceCatalogRepository());
        catalog.OpenProject(root);
        var one = new TransportResource { Id = "can_resource_1", Name = "CAN 1", Type = "can", LeaseMode = "shared_bus" };
        var two = new TransportResource { Id = "can_resource_2", Name = "CAN 2", Type = "can", LeaseMode = "shared_bus" };
        TransportSettingsAccessor.Write(one, new(CanChannel: "CAN0", CanBitrate: 500000));
        TransportSettingsAccessor.Write(two, new(CanChannel: "CAN0", CanBitrate: 500000));
        catalog.Catalog.Transports.Add(one); catalog.Catalog.Transports.Add(two); catalog.Save(catalog.Catalog);
        using var manager = new ResourceManager(catalog);
        using var lease1 = manager.Acquire(one.Id, "device:can1");
        using var lease2 = manager.Acquire(two.Id, "device:can2");
        Check(manager.IsLeased(one.Id) && manager.IsLeased(two.Id), "CAN 同一物理通道允许多个共享租约");
    }

    private static async Task VerifyVisaAsync()
    {
        var backend = new SimulatedVisaBackend();
        var resource = new TransportResource { Id = "visa_probe", Name = "VISA probe", Type = "visa" };
        TransportSettingsAccessor.Write(resource, new(VisaResource: "SIM::VISA0", VisaBackend: "simulated"));
        backend.EnqueueResponse("SIM::VISA0", Encoding.ASCII.GetBytes("OK"));
        var session = ((ITransportSessionFactory)new TransportSessionFactory(backend)).CreateByte(resource);
        await using (session)
        {
            await session.OpenAsync(new(resource.Id, resource.Type, resource.Settings));
            var response = await session.ExchangeAsync(Encoding.ASCII.GetBytes("*IDN?\n"), true, TimeSpan.FromMilliseconds(100));
            Check(Encoding.ASCII.GetString(response!.Value.Span) == "OK", "VISA 模拟后端打开、读写和响应");
        }
        var unavailable = ((ITransportSessionFactory)new TransportSessionFactory()).CreateByte(resource);
        try { await unavailable.OpenAsync(new(resource.Id, resource.Type, resource.Settings)); throw new InvalidOperationException("缺少 VISA 后端未报错"); }
        catch (NotSupportedException) { Check(true, "缺少 VISA 后端明确报错"); }
        finally { await unavailable.DisposeAsync(); }
    }
}
