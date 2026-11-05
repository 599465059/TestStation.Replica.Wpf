using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestStation.App.Devices.Reference;
using TestStation.App.Services;
using TestStation.Application;
using TestStation.Application.Drivers;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain;
using TestStation.Domain.Devices;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Devices.Reference;
using TestStation.Infrastructure.Protocols.Modbus;
using TestStation.Infrastructure.Resources;
using TestStation.Infrastructure.Transports;
using ThemeMode = TestStation.App.Services.ThemeMode;

internal static class ReferenceRuntimeAcceptance
{
    private static int _checks;
    private static void Check(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); _checks++; Console.WriteLine("PASS " + name); }
    private static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or ArgumentException or InvalidDataException or IOException or TimeoutException or NotSupportedException) { Check(true, name + ": " + e.Message); return; } throw new InvalidOperationException(name + " 未拒绝"); }
    private static JsonElement J<T>(T value) => JsonSerializer.SerializeToElement(value);
    private static async Task Until(Func<bool> condition, string name)
    { for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20); Check(condition(), name); }

    public static async Task RunAsync(string output)
    {
        await VerifyFramingAsync();
        await VerifyRollbackAsync();
        await VerifyDeviceRuntimeAsync(output);
        Console.WriteLine($"REFERENCE_RESULT=PASS {_checks} checks");
        File.WriteAllText(Path.Combine(output, "reference-summary.txt"), $"PASS {_checks} checks");
    }

    private static async Task VerifyFramingAsync()
    {
        async Task Probe(Func<ModbusProtocolSession, Task> operation, Func<byte[], byte[]> respond, bool tcp = true)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var server = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                    var stream = client.GetStream(); var request = new byte[tcp ? 12 : 8];
                    await stream.ReadExactlyAsync(request, deadline.Token);
                    var bytes = respond(request);
                    // Fragment both headers and payloads with delays longer than the previous 20 ms heuristic.
                    foreach (var part in bytes.Chunk(2))
                    { await stream.WriteAsync(part, deadline.Token); await Task.Delay(35, deadline.Token); }
                });
                var resource = new TransportResource { Id = "framing", Name = "frame probe", Type = "tcp" };
                TransportSettingsAccessor.Write(resource, new(Host: "127.0.0.1", TcpPort: ((IPEndPoint)listener.LocalEndpoint).Port));
                await using var protocol = new ModbusProtocolSession(new TcpTransportSession(), resource, useTcp: tcp);
                await protocol.OpenAsync(deadline.Token);
                await operation(protocol);
                await server;
            }
            finally { listener.Stop(); }
        }
        byte[] ReadResponse(byte[] request)
        {
            var response = new byte[] { request[0], request[1], 0, 0, 0, 5, 1, 3, 2, 0x12, 0x34 };
            return response;
        }
        await Probe(async p => Check((await p.ReadHoldingAsync(0, 1))[0] == 0x1234, "Modbus TCP 分片头和负载完整接收"), ReadResponse);
        await Probe(p => Reject(() => p.ReadHoldingAsync(0, 1), "拒绝事务 ID 不匹配"), q => { var r = ReadResponse(q); r[1]++; return r; });
        await Probe(p => Reject(() => p.WriteSingleAsync(7, 12), "拒绝写入确认地址或值不匹配"), q => { q[^1]++; return q; });
        await Probe(p => Reject(() => p.ReadHoldingAsync(0, 1), "明确报告短 Modbus 异常帧"), q => [q[0], q[1], 0, 0, 0, 3, 1, 0x83, 2]);
        await Probe(p => Reject(() => p.ReadHoldingAsync(0, 1), "拒绝 MBAP 非法长度"), q => [q[0], q[1], 0, 0, 0xFF, 0xFF]);
        await Probe(async p => { await p.WriteCoilAsync(1, true); Check(true, "RTU 写线圈使用功能码 05 并接收完整确认"); },
            q => { Check(q[1] == 5 && q[4] == 0xFF, "RTU 线圈真实请求内容"); return q; }, tcp: false);
    }

    private static async Task VerifyRollbackAsync()
    {
        var one = new Backend(); var two = new Backend { FailBegin = true };
        await Reject(() => AutomationBackendLifecycle.BeginAsync([one, two], [], default), "组合后端获取失败");
        Check(one.Ended == 1 && two.Ended == 1, "获取失败同时回滚已获取和失败的后端");
        one.FailEnd = true;
        try { await AutomationBackendLifecycle.EndAsync([two, one]); }
        catch (AggregateException) { }
        Check(one.Ended == 2 && two.Ended == 2, "一个后端清理异常仍清理其他后端");
    }

    private static async Task VerifyDeviceRuntimeAsync(string output)
    {
        await using var first = new ScpiServer();
        await using var second = new ScpiServer();
        var catalog = new ResourceCatalogService(new ResourceCatalogRepository());
        catalog.OpenProject(Path.Combine(output, "reference-project"));
        using var resources = new ResourceManager(catalog);
        await using var service = new ReferenceScpiDeviceService(catalog, resources, new TransportSessionFactory());
        var family = service.Families.Single(f => f.Id == "reference.scpi.IT6000");
        var device1 = family.Add("设备一"); var device2 = family.Add("设备二"); var conflict = family.Add("冲突实例");
        var r1 = Resource("reference1", first.Port); var r2 = Resource("reference2", second.Port);
        var data = catalog.Catalog; data.Transports.AddRange([r1, r2]); catalog.Save(data);
        family.Update(device1.Id, device1.Name, "IT6005", r1.Id); family.Update(device2.Id, device2.Name, "IT6005", r2.Id);
        family.Update(conflict.Id, conflict.Name, "IT6005", r1.Id);
        var backend = new CompositeDeviceIoBackend(service);
        using var runtime = new SimulatedChannelRuntimeCoordinator(service, backend);
        var c1 = device1.Id + "."; var c2 = device2.Id + ".";
        var vm = new ReferenceScpiViewModel(family.Id, service, runtime, resources, new Dialogs());
        var vm2 = new ReferenceScpiViewModel(family.Id, service, runtime, resources, new Dialogs(), device1.Id);
        Check(vm2.SelectedDevice?.Id == device1.Id && !vm2.CanSelectInstance && !vm2.IsFamilyPanel,
            "按实例创建手动面板，固定设备选择并隐藏实例管理入口");
        vm.SelectDevice(device1.Id); vm2.SelectDevice(device1.Id);
        var view = new ReferenceScpiView(vm); var view2 = new ReferenceScpiView(vm2);
        var window = new Window { Title = "设备运行闭环短验收", Content = view, Width = 1280, Height = 760 };
        var window2 = new Window { Title = "共享设备面板", Content = view2, Width = 1050, Height = 650 };
        try
        {
            window.Show(); window2.Show();
            await vm.ConnectCommand.ExecuteAsync(null);
            await Until(() => vm.Fields.First(f => f.Id == c1 + "ReadVoltage").LastValidTimestamp.HasValue, "连接后面板保持可见并启动测量轮询");
            Check(first.Connections == 1, "同一设备多面板共享单一连接");
            var v = vm.Fields.Single(f => f.Id == c1 + "SetVoltage");
            v.Input = "12.5"; await vm.WriteFieldCommand.ExecuteAsync(v);
            Check(first.Voltage == 12.5, "面板写入命令通过协调器发送真实 SCPI");
            await vm.WriteFieldCommand.ExecuteAsync(vm.Fields.Single(f => f.Id == c1 + "action.StartOutput"));
            Check(first.Output, "面板动作按钮执行定义的 SCPI 指令");
            await Reject(() => service.WriteAsync(c1 + "SetVoltage", J(1e9)), "越界写入被服务层拒绝");
            await Reject(() => service.WriteAsync(c1 + "SetVoltage", J("12")), "类型不匹配被服务层拒绝");
            await Reject(() => service.WriteAsync(c1 + "ReadVoltage", J(1)), "只读通道拒绝写入");
            await Reject(() => service.ReadAsync(c1 + "missing"), "未知通道明确报错");
            Check(service.StateFor(device1.Id).Connected, "参数校验错误不会破坏有效连接");
            var read = vm.Fields.Single(f => f.Id == c1 + "ReadVoltage");
            await vm.ReadFieldCommand.ExecuteAsync(read);
            var last = read.Display;
            Check(first.Commands.All(q => !q.Equals("VOLTage?", StringComparison.OrdinalIgnoreCase)) == false, "写入后按需读取设定值");
            var setQueries = first.Commands.Count(q => q.Equals("VOLTage?", StringComparison.OrdinalIgnoreCase));
            await Task.Delay(550);
            Check(first.Commands.Count(q => q.Equals("VOLTage?", StringComparison.OrdinalIgnoreCase)) == setQueries, "默认仅轮询测量通道，不轮询设定值");
            using (var held = resources.Acquire(r2.Id, "occupied"))
                await Reject(() => runtime.AcquireAutomationAsync("automatic", [c2 + "SetVoltage"]), "资源冲突自动建连失败");
            Check(!service.IsAutomationActive && service.StateFor(device1.Id).Connected, "自动获取失败回滚占用且保留原手动连接");
            await Reject(() => runtime.AcquireAutomationAsync("automatic", [c2 + "SetVoltage", conflict.Id + ".SetVoltage"]), "第二台建连失败时回滚先前新开的会话");
            Check(!resources.IsLeased(r2.Id) && !service.StateFor(device2.Id).Connected && service.StateFor(device1.Id).Connected, "部分获取失败不遗留临时连接且保留原手动连接");
            await using (var automation = new ChannelAutomationScope(await runtime.AcquireAutomationAsync("automatic", [c1 + "ReadVoltage", c2 + "SetVoltage", c2 + "ReadVoltage"])))
            {
                Check(service.StateFor(device2.Id).Connected, "自动执行成功打开原来未连接的设备");
                await Reject(() => runtime.WriteAsync(c1 + "SetVoltage", J(5), "manual"), "自动期间禁止手动写入");
                await Reject(() => family.ConnectAsync(device1.Id), "自动期间禁止手动修改连接");
                var queryCount = first.Commands.Count(q => q.Contains('?'));
                await Task.Delay(550);
                Check(first.Commands.Count(q => q.Contains('?')) == queryCount, "自动占用期间手动轮询停止");
                var sequence = new SequenceDefinition
                {
                    Id = "reference_short", Name = "参考设备短序列", Role = "main",
                    Steps = [
                        new() { Id = "set", Kind = "set_value", Params = new() { ["channel_id"] = J(c2 + "SetVoltage"), ["value"] = J(7.5) } },
                        new() { Id = "read", Kind = "read_value", Params = new() { ["channel_id"] = J(c2 + "ReadVoltage") } },
                        new() { Id = "assert", Kind = "assert", Params = new() { ["channel_id"] = J(c2 + "ReadVoltage"), ["comparator"] = J("=="), ["expected"] = J(7.5) } }
                    ]
                };
                var run = await new OpenTapHostClient(runtime).RunAsync(sequence, [sequence], new Dictionary<string, object?>(), new Dictionary<string, object?>());
                Check(run.Result.Status == "completed" && second.Voltage == 7.5 && first.Voltage == 12.5, "短 OpenTAP 序列跨进程调用正确设备且两个实例隔离");
            }
            Check(!resources.IsLeased(r2.Id) && !service.StateFor(device2.Id).Connected, "自动结束关闭临时会话并释放资源");
            var afterAuto = first.Commands.Count;
            await Until(() => first.Commands.Count > afterAuto, "自动结束恢复可见且已连接面板的轮询");
            window2.Hide(); window.Hide();
            await Task.Delay(150);
            var hiddenCount = first.Commands.Count;
            await Task.Delay(550);
            Check(first.Commands.Count == hiddenCount, "所有面板隐藏后停止轮询");
            window.Show();
            await Until(() => first.Commands.Count > hiddenCount, "面板重新显示后恢复轮询");
            var theme = new ThemeService();
            foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
            {
                theme.Apply(mode, AccentPalette.Teal); await Task.Delay(80);
                Capture(window, Path.Combine(output, "reference-" + mode + ".png"));
            }
            Check(vm.Fields.All(f => f.Definition.CanRead || f.Definition.CanWrite), "前台包含读取、写入及动作通道");
            vm.AutoRefresh = false; vm2.AutoRefresh = false;
            first.InvalidResponse = true;
            await vm.ReadFieldCommand.ExecuteAsync(read);
            Check(read.Display == last && read.Quality.Contains("错误") && !resources.IsLeased(r1.Id), "非法响应保留最后有效值并断开释放资源");
            first.InvalidResponse = false;
            await family.ConnectAsync(device1.Id);
            first.IgnoreQueries = true;
            await Reject(() => service.ReadAsync(c1 + "ReadVoltage"), "设备查询超时明确报错");
            Check(!resources.IsLeased(r1.Id) && !service.StateFor(device1.Id).Connected, "超时释放连接及独占租约");
            first.IgnoreQueries = false;
            await family.ConnectAsync(device1.Id);
            using (var cancel = new CancellationTokenSource(60))
            {
                first.IgnoreQueries = true;
                try { await service.ReadAsync(c1 + "ReadVoltage", cancel.Token); }
                catch (OperationCanceledException) { }
                finally { first.IgnoreQueries = false; }
            }
            Check(!resources.IsLeased(r1.Id), "取消正在等待响应的事务后释放租约");
            await family.ConnectAsync(device1.Id);
            var registry = new ManualPluginViewRegistry();
            registry.Register(new ReferenceScpiPanelProvider(service, resources, new Dialogs(), family.Id));
            registry.BeforeEnabledChangedAsync = (id, enabled) => service.SetFamilyEnabledAsync(id, enabled);
            registry.ExecuteChangeAsync = operation => runtime.ExecuteManualAsync(operation);
            var disconnectedBeforeNotify = false;
            registry.Changed += (_, _) => disconnectedBeforeNotify = !service.StateFor(device1.Id).Connected && !resources.IsLeased(r1.Id);
            await registry.SetEnabledAsync(family.Id, false);
            Check(disconnectedBeforeNotify && !registry.IsEnabled(family.Id), "插件禁用完成断开后才发布界面注销通知");
            Check(!resources.IsLeased(r1.Id) && !service.StateFor(device1.Id).Connected && service.GetChannels().Count == 0, "禁用家族等待断开并注销自动通道");
            await Reject(() => family.ConnectAsync(device1.Id), "禁用家族不能重新连接");
            await using var pending = new ReferenceProtocolDeviceService(catalog, resources, new TransportSessionFactory());
            var pendingFamily = pending.Families.Single(f => f.Id == "reference.modbus.C3000");
            var d = pendingFamily.Add("待迁移"); pendingFamily.Update(d.Id, d.Name, pendingFamily.Models[0].Id, r2.Id);
            await Reject(() => pendingFamily.ConnectAsync(d.Id), "缺少真实映射的设备禁止连接");
            Check(!resources.IsLeased(r2.Id) && pending.GetChannels().Count == 0, "占位设备不占资源也不发布通用寄存器通道");
            using (var emptyVm = new ReferenceScpiViewModel(pendingFamily.Id, pending, runtime, resources, new Dialogs()))
            {
                var emptyWindow = new Window { Content = new ReferenceScpiView(emptyVm), Width = 1100, Height = 700 };
                try
                {
                    emptyWindow.Show();
                    foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
                    { theme.Apply(mode, AccentPalette.Teal); await Task.Delay(60); Capture(emptyWindow, Path.Combine(output, "reference-empty-" + mode + ".png")); }
                    Check(!emptyVm.CanConnect && emptyVm.Fields.Count == 0 && emptyVm.ModelStatus.Contains("不可连接"), "未完成型号显示明确空状态且禁用连接按钮");
                }
                finally { emptyWindow.Close(); }
            }
            await using var failing = new ReferenceScpiDeviceService(catalog, resources, new FailingFactory());
            try { await failing.Families.Single(f => f.Id == family.Id).ConnectAsync(device1.Id); }
            catch (IOException error) { Check(error.Message == "open failed" && error.Data.Contains("ConnectionCleanup"), "连接和释放同时失败时保留原始错误及清理诊断"); }
            Check(!resources.IsLeased(r1.Id) && !failing.StateFor(device1.Id).Connected, "连接失败且 Dispose 抛异常仍释放租约");
            Check(second.Commands.Any(q => q.StartsWith("VOLT", StringComparison.OrdinalIgnoreCase)), "通信记录包含实际 SCPI 字符串");
            File.WriteAllLines(Path.Combine(output, "reference-scpi-traffic.txt"), first.Commands.Select(q => "first " + q).Concat(second.Commands.Select(q => "second " + q)));
        }
        finally { window.Close(); window2.Close(); vm.Dispose(); vm2.Dispose(); }
    }

    private static TransportResource Resource(string id, int port)
    {
        var resource = new TransportResource { Id = id, Name = id, Type = "tcp" };
        TransportSettingsAccessor.Write(resource, new(Host: "127.0.0.1", TcpPort: port, ConnectTimeoutMs: 500));
        return resource;
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class ScpiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _accept;
        public readonly ConcurrentQueue<string> Commands = new();
        public double Voltage;
        public bool Output;
        public volatile bool InvalidResponse;
        public volatile bool IgnoreQueries;
        public int Connections;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public ScpiServer() { _listener.Start(); _accept = AcceptAsync(); }
        private async Task AcceptAsync()
        {
            try { while (true) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); Interlocked.Increment(ref Connections); _clients.Add(HandleAsync(client)); } }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII);
                try
                {
                    while (await reader.ReadLineAsync(_stop.Token) is { } command)
                    {
                        Commands.Enqueue(command);
                        var upper = command.ToUpperInvariant();
                        if (upper.StartsWith("VOLTAGE ")) Voltage = double.Parse(command.Split(' ', 2)[1], CultureInfo.InvariantCulture);
                        if (upper.StartsWith("OUTPUT ")) Output = upper.EndsWith("ON") || upper.EndsWith("1");
                        if (!command.Contains('?') || IgnoreQueries) continue;
                        var response = InvalidResponse ? "invalid" : upper.StartsWith("OUT") ? Output ? "1" : "0"
                            : upper.Contains("VOLT") ? Voltage.ToString(CultureInfo.InvariantCulture) : "0";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(response + "\n"), _stop.Token);
                    }
                }
                catch (Exception e) when (e is IOException or OperationCanceledException) { }
            }
        }
        public async ValueTask DisposeAsync() { _stop.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_clients); _stop.Dispose(); }
    }
    private sealed class FailingFactory : ITransportSessionFactory
    {
        public ITransportSession Create(TransportResource resource) => new FailingTransport();
        private sealed class FailingTransport : ByteTransportSessionBase
        {
            public FailingTransport() : base("tcp") { }
            public override Task OpenAsync(TransportEndpoint endpoint, CancellationToken token = default) => Task.FromException(new IOException("open failed"));
            public override Task CloseAsync(CancellationToken token = default) => Task.CompletedTask;
            public override Task<ReadOnlyMemory<byte>?> ExchangeAsync(ReadOnlyMemory<byte> request, bool expect, TimeSpan timeout, CancellationToken token = default) => throw new NotSupportedException();
            public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("dispose failed"));
        }
    }
    private sealed class Backend : IAutomationChannelBackend
    {
        public bool FailBegin, FailEnd; public int Ended;
        public bool IsAutomationActive { get; set; }
        public IReadOnlyList<string> ChannelIds => [];
        public bool OwnsChannel(string id) => false;
        public IReadOnlyList<SequenceChannelDefinition> GetChannels() => [];
        public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
        public Task<JsonElement> ReadAsync(string id, CancellationToken token = default) => throw new NotSupportedException();
        public Task WriteAsync(string id, JsonElement value, CancellationToken token = default) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken token = default) => Task.CompletedTask;
        public Task BeginAutomationAsync(IEnumerable<string> ids, CancellationToken token = default) => FailBegin ? Task.FromException(new IOException("连接失败")) : Task.CompletedTask;
        public Task EndAutomationAsync(CancellationToken token = default) { Ended++; return FailEnd ? Task.FromException(new IOException("断开失败")) : Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Dialogs : ISequenceDialogService
    {
        public Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name) => Task.FromResult(SequenceUnsavedChoice.Cancel);
        public Task<bool> ConfirmDeleteSequenceAsync(string name) => Task.FromResult(false);
        public Task<string?> RenameSequenceAsync(string name) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmStopExecutionAsync() => Task.FromResult(true);
    }
}
