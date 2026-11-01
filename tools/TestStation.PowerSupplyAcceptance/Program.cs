using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Industrial.Controls;
using TestStation.App.Devices.ItechPowerSupply;
using TestStation.App.Devices.ItechPowerSupply.ViewModels;
using TestStation.App.Devices.ItechPowerSupply.Views;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.App.Views;
using TestStation.Application;
using TestStation.Application.Drivers;
using TestStation.Application.Hardware;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Application.Resources;
using TestStation.Domain;
using TestStation.Domain.Devices.ItechPowerSupply;
using TestStation.Domain.Resources;
using TestStation.Infrastructure;
using TestStation.Infrastructure.Devices.ItechPowerSupply;
using TestStation.Infrastructure.Resources;
using TestStation.Infrastructure.Transports;
using TestStation.Infrastructure.Transports.Can.Pcan;
using ThemeMode = TestStation.App.Services.ThemeMode;

internal static class Program
{
    private static readonly List<object> Checks = [];
    private static string _output = "";
    [STAThread] public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? Environment.CurrentDirectory);
        _output = Path.Combine(root, "artifacts", "power-supply-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(_output);
        Environment.SetEnvironmentVariable("TESTSTATION_OPENTAP_HOST", Path.Combine(root, "src", "TestStation.App", "bin", "Debug", "net10.0-windows", "OpenTapHost", "TestStation.OpenTapHost.dll"));
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        var result = 0;
        app.Startup += async (_, _) =>
        {
            var clock = Stopwatch.StartNew();
            try { await Run(root, args); }
            catch (Exception e) { result = 1; File.WriteAllText(Path.Combine(_output, "failure.txt"), e.ToString()); Console.Error.WriteLine(e); }
            finally { File.WriteAllText(Path.Combine(_output, "summary.json"), JsonSerializer.Serialize(new { passed = result == 0, seconds = clock.Elapsed.TotalSeconds, checks = Checks }, new JsonSerializerOptions { WriteIndented = true })); Console.WriteLine($"RESULT={(result == 0 ? "PASS" : "FAIL")} {Checks.Count} checks | {_output}"); app.Shutdown(result); }
        };
        app.Run(); return result;
    }
    private static void Check(bool ok, string name)
    { Checks.Add(new { name, passed = ok }); Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) throw new InvalidOperationException(name); }
    private static async Task Reject(Func<Task> action, string name)
    { try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or FormatException or InvalidDataException) { Check(true, name + ": " + e.Message); return; } Check(false, name); }
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static int Port() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static Process Simulator(string root, int port, string name)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root,"tools","TestStation.VirtualPowerSupply","bin","Debug","net10.0","TestStation.VirtualPowerSupply.dll"), "--port", port.ToString(), "--log", Path.Combine(_output, name + ".log") }) start.ArgumentList.Add(arg);
        var process = Process.Start(start)!; process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { }; process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
    }
    private static async Task Run(string root, string[] args)
    {
        if (args.Contains("--real-serial", StringComparer.OrdinalIgnoreCase))
        {
            await RunRealSerialAsync(root, args);
            return;
        }
        if (args.Contains("--real-can", StringComparer.OrdinalIgnoreCase))
        {
            await RunRealCanAsync(root, args);
            return;
        }
        var one = Port(); var two = Port(); while (two == one) two = Port();
        using var first = Simulator(root, one, "supply-one"); using var second = Simulator(root, two, "supply-two");
        Window? window = null;
        try
        {
            await Task.Delay(300);
            var legacyPath = Path.Combine(_output,"legacy-profile.json"); File.WriteAllText(legacyPath,JsonSerializer.Serialize(ItechPowerSupplyProfile.PublicScpi));
            Check(new ItechPowerSupplyProfileStore(legacyPath).Family.Channels.Any(c => c.Id == "Voltage.Setpoint" && c.Binding == "voltage"), "旧型号配置迁移保留功能绑定");
            var store = new ItechPowerSupplyProfileStore(Path.Combine(_output,"family.json"));
            var hiokiValues = HiokiPowerAnalyzerCatalog.Parse("230.1,1.25,287.6,300.2,85.0,0.958,12.5,50.0");
            Check(hiokiValues.Length == 8 && Math.Abs(hiokiValues[0] - 230.1) < 0.001 && HiokiPowerAnalyzerCatalog.Query(6).Contains("Urms6"), "HIOKI PW6001 SCPI 查询与响应解析");
            var family = store.Family; family.Models.Add(new() { Id="compact", Name="24 V 模拟配置", MaxVoltage=24, MaxCurrent=3 });
            family.Channels.Add(new("Identity", "设备身份", "string", "", true, false, "*IDN?"));
            family.Channels.Add(new("Status.Register", "事件寄存器", "integer", "", true, false, "*ESR?"));
            family.Channels.Add(new("Invalid.Boolean", "错误响应演示", "boolean", "", true, false, "*IDN?"));
            store.SaveFamily(family);
            Check(new ItechPowerSupplyProfileStore(Path.Combine(_output,"family.json")).Family.Models.Any(model => model.Id == "compact"), "家族原子保存与重新加载");
            var invalid = store.Family; invalid.Channels[0].Scale = 0; await Reject(() => Sync(() => PowerSupplyValidation.Validate(invalid)), "非法换算配置被拒绝");
            var catalog = new ResourceCatalogService(new ResourceCatalogRepository()); using var resources = new ResourceManager(catalog);
            await using var devices = new PowerSupplyDeviceService(catalog, resources, new TransportSessionFactory(), store);
            catalog.OpenProject(Path.Combine(_output,"project"));
            Check(devices.Devices.Count == 0, "新工程不自动创建设备实例");
            var firstDevice = devices.AddDevice("电源 01");
            Check(firstDevice.Id == "ITECH01", "新建设备使用兼容实例标识 ITECH01");
            var resource1 = new TransportResource { Id = "first_tcp", Name = "第一台虚拟电源", Type = "tcp" };
            TransportSettingsAccessor.Write(resource1, new(Host: "127.0.0.1", TcpPort: one, ConnectTimeoutMs: 800)); devices.SaveResource(resource1);
            devices.UpdateDevice(firstDevice.Id, firstDevice.Name, "compact", resource1.Id);
            var resource2 = new TransportResource { Id="second_tcp", Name="第二台虚拟电源", Type="tcp" }; TransportSettingsAccessor.Write(resource2,new(Host:"127.0.0.1", TcpPort:two,ConnectTimeoutMs:800)); devices.SaveResource(resource2);
            var d2 = devices.AddDevice("电源 02"); devices.UpdateDevice(d2.Id,d2.Name,"compact",resource2.Id);
            Check(devices.GetChannels().Any(c=>c.Id=="ITECH02.Voltage.Setpoint"), "第二实例自动注册独立通道");
            var registry = new ManualPluginViewRegistry(); var dialogs = new Dialogs();
            var provider = new SimulatedPluginChannelProvider(registry, new TestStation.SimulatedPlugins.SimulatedDeviceModelCatalog(), store, devices);
            using var runtime = new SimulatedChannelRuntimeCoordinator(provider, devices);
            var panelProvider = new PowerSupplyPanelProvider(devices,store,resources,dialogs); registry.Register(panelProvider);
            var familyView = panelProvider.CreateView(new("device-testing", "设备测试", runtime));
            var uiWindow = new Window { Content = familyView, Width = 1100, Height = 760, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            uiWindow.Show(); uiWindow.UpdateLayout();
            var navButtons = FindVisual<Button>(uiWindow).Where(button => button.Content is "设备操作" or "家族配置").ToArray();
            Check(navButtons.Length == 2, "前后台页面入口加载");
            navButtons.Single(button => button.Content is "家族配置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); uiWindow.UpdateLayout();
            Check(FindVisual<ListBox>(uiWindow).Any(list => list.Items.Count == 4) && FindVisual<DataGrid>(uiWindow).Any(), "后台四类导航和编辑表格加载");
            uiWindow.Close();
            await devices.ConnectAsync("ITECH01"); await devices.ConnectAsync("ITECH02");
            Check(devices.ConnectionFor("ITECH01").Connected && devices.ConnectionFor("ITECH02").Connected, "两台 TCP 电源独立连接");
            await runtime.WriteAsync("ITECH01.Voltage.Setpoint",JsonSerializer.SerializeToElement(12.0),"manual");
            await runtime.WriteAsync("ITECH02.Voltage.Setpoint",JsonSerializer.SerializeToElement(5.0),"manual");
            Check((await runtime.ReadOnceAsync("ITECH01.Voltage.Setpoint","manual")).Value.GetDouble()==12 && (await runtime.ReadOnceAsync("ITECH02.Voltage.Setpoint","manual")).Value.GetDouble()==5, "不同端口设置与读数互不串台");
            await Reject(()=>runtime.WriteAsync("ITECH02.Voltage.Setpoint",JsonSerializer.SerializeToElement(30.0),"manual"),"型号量程阻止越界写入");
            Check((await runtime.ReadOnceAsync("ITECH01.Identity","manual")).Value.ValueKind==JsonValueKind.String && (await runtime.ReadOnceAsync("ITECH01.Status.Register","manual")).Value.GetInt64()>=0,"文本与整数通道真实响应解析");
            await Reject(()=>Sync(()=>{ var r=devices.Resources.First(r=>r.Id==resource1.Id); r.Name="不能保存"; devices.SaveResource(r); }),"已连接资源不能修改");
            await Reject(()=>Sync(()=>devices.RemoveDevice("ITECH01")),"已连接设备不能删除");
            var duplicate = new TransportResource { Id="alias_tcp", Name="同端点别名", Type="tcp", Settings=new(resource1.Settings) }; devices.SaveResource(duplicate);
            var d3=devices.AddDevice("冲突设备"); devices.UpdateDevice(d3.Id,d3.Name,"public-scpi",duplicate.Id);
            await Reject(()=>devices.ConnectAsync(d3.Id),"同一物理 TCP 端点冲突被阻止"); devices.RemoveDevice(d3.Id);
            using (var lease1=resources.Acquire(resource2.Id,"device:ITECH02")) { using var lease2=resources.Acquire(resource2.Id,"device:ITECH02"); Check(resources.IsLeased(resource2.Id),"同一设备重复租约计数正确"); }
            await devices.DisconnectDeviceAsync("ITECH01"); await devices.DisconnectDeviceAsync("ITECH02");
            using (var automation = await runtime.AcquireAutomationAsync("automatic", new[] { "ITECH01.Voltage.Measurement", "ITECH02.Voltage.Measurement" }))
            { Check(devices.ConnectionFor("ITECH01").Connected && devices.ConnectionFor("ITECH02").Connected, "自动租约按通道自动连接设备"); await runtime.ReadOnceAsync("ITECH01.Voltage.Measurement", "automatic"); await Reject(() => runtime.ReadOnceAsync("ITECH01.Voltage.Measurement", "manual"), "自动占用暂停手动读取"); }
            Check(!runtime.IsAutomationActive, "自动租约结束恢复手动访问状态");
        }
        finally { window?.Close(); if(!first.HasExited) first.Kill(true); if(!second.HasExited) second.Kill(true); }
    }

    /// <summary>
    /// Exercises the same serial transport and device service used by the WPF
    /// application. COM3 is the real station port; COM6 is opened by this
    /// process as a small SCPI instrument emulator. The two ports must be
    /// connected through the user's RS485 adapters.
    /// </summary>
    private static async Task RunRealSerialAsync(string root, string[] args)
    {
        var stationPort = Option(args, "--station-port", "COM3");
        var emulatorPort = Option(args, "--emulator-port", "COM6");
        var baud = int.Parse(Option(args, "--baud", "115200"));
        var output = Path.Combine(root, "artifacts", "real-serial-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        using var stop = new CancellationTokenSource();
        var emulator = Task.Run(() => RunSerialEmulatorAsync(emulatorPort, baud, Path.Combine(output, "emulator.log"), stop.Token));
        var catalog = new ResourceCatalogService(new ResourceCatalogRepository());
        var project = Path.Combine(output, "project");
        using var resources = new ResourceManager(catalog);
        var profilePath = Path.Combine(output, "family.json");
        var profiles = new ItechPowerSupplyProfileStore(profilePath);
        await using var devices = new PowerSupplyDeviceService(catalog, resources, new TransportSessionFactory(), profiles);
        catalog.OpenProject(project);
        var device = devices.AddDevice("RS485 实物电源");
        var resource = new TransportResource { Id = "rs485_station", Name = $"RS485 {stationPort}", Type = "serial" };
        TransportSettingsAccessor.Write(resource, new(
            Port: stationPort, BaudRate: baud, DataBits: 8, Parity: "None", StopBits: "1",
            SerialMode: "RS485", FlowControl: "None",
            HalfDuplex: true, DirectionDelayMs: 2));
        resource.Settings["timeout_ms"] = JsonSerializer.SerializeToElement(1500);
        devices.SaveResource(resource);
        devices.UpdateDevice(device.Id, device.Name, "public-scpi", resource.Id);
        var provider = new SimulatedPluginChannelProvider(null, new TestStation.SimulatedPlugins.SimulatedDeviceModelCatalog(), profiles, devices);
        using var runtime = new SimulatedChannelRuntimeCoordinator(provider, devices);
        try
        {
            await Task.Delay(250);
            await devices.ConnectAsync(device.Id);
            Check(devices.ConnectionFor(device.Id).Connected, $"真实串口 {stationPort} 连接模拟设备 {emulatorPort}");
            await runtime.WriteAsync($"{device.Id}.Voltage.Setpoint", JsonSerializer.SerializeToElement(12.5), "manual");
            await runtime.WriteAsync($"{device.Id}.Output.Enabled", JsonSerializer.SerializeToElement(true), "manual");
            var direct = await runtime.ReadOnceAsync($"{device.Id}.Voltage.Measurement", "manual");
            Check(Math.Abs(direct.Value.GetDouble() - 12.5) < 0.001, "真实 RS485 直接读写及响应解析");

            var sequence = new SequenceDefinition
            {
                Id = "real_serial_main", Name = "真实 RS485 自动调用", Role = "main",
                Steps = [
                    new SequenceStep { Id = "set", Kind = "set_value", Name = "设置电压", Params = new()
                    {
                        ["channel_id"] = JsonSerializer.SerializeToElement($"{device.Id}.Voltage.Setpoint"),
                        ["value"] = JsonSerializer.SerializeToElement(18.0)
                    } },
                    new SequenceStep { Id = "output", Kind = "set_value", Name = "打开输出", Params = new()
                    {
                        ["channel_id"] = JsonSerializer.SerializeToElement($"{device.Id}.Output.Enabled"),
                        ["value"] = JsonSerializer.SerializeToElement(true)
                    } },
                    new SequenceStep { Id = "read", Kind = "read_value", Name = "读取电压", Params = new()
                    {
                        ["channel_id"] = JsonSerializer.SerializeToElement($"{device.Id}.Voltage.Measurement")
                    } },
                    new SequenceStep { Id = "assert", Kind = "assert", Name = "验证真实值", Params = new()
                    {
                        ["channel_id"] = JsonSerializer.SerializeToElement($"{device.Id}.Voltage.Measurement"),
                        ["comparator"] = JsonSerializer.SerializeToElement("=="),
                        ["expected"] = JsonSerializer.SerializeToElement(18.0)
                    } }
                ]
            };
            var host = new OpenTapHostClient(runtime);
            var run = await host.RunAsync(sequence, [sequence], new Dictionary<string, object?>(), new Dictionary<string, object?>());
            Check(run.Result.Status == "completed", "OpenTAP 自动序列通过真实 RS485 设备服务");
            await devices.DisconnectDeviceAsync(device.Id);
            Check(!devices.ConnectionFor(device.Id).Connected, "自动测试后释放真实串口会话");
            File.WriteAllText(Path.Combine(output, "summary.txt"), "PASS\n");
            Console.WriteLine($"RESULT=PASS real serial acceptance | {output}");
        }
        finally
        {
            stop.Cancel();
            try { await emulator; } catch (OperationCanceledException) { }
        }
    }

    private static async Task RunRealCanAsync(string root, string[] args)
    {
        var firstChannel = Option(args, "--can-tx", "PCAN_USBBUS1");
        var secondChannel = Option(args, "--can-rx", "PCAN_USBBUS2");
        var output = Path.Combine(root, "artifacts", "real-can-acceptance", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var adapter = new PcanVendorAdapter();
        var backend = new RealCanAutomationBackend(new TransportSessionFactory(canAdapters: [adapter]), firstChannel, secondChannel);
        using var runtime = new SimulatedChannelRuntimeCoordinator(backend, backend);
        var sequence = new SequenceDefinition
        {
            Id = "real_can_main", Name = "真实 PCAN 自动调用", Role = "main",
            Steps = [
                new SequenceStep { Id = "send", Kind = "set_value", Name = "发送 CAN 帧", Params = new()
                {
                    ["channel_id"] = JsonSerializer.SerializeToElement("CAN.Tx"),
                    ["value"] = JsonSerializer.SerializeToElement(new[] { 0xDE, 0xAD, 0xBE, 0xEF })
                } },
                new SequenceStep { Id = "receive", Kind = "read_value", Name = "接收 CAN 帧", Params = new()
                {
                    ["channel_id"] = JsonSerializer.SerializeToElement("CAN.Rx")
                } }
            ]
        };
        var host = new OpenTapHostClient(runtime);
        try
        {
            using var automation = await runtime.AcquireAutomationAsync("automatic", ["CAN.Tx", "CAN.Rx"]);
            var runTask = host.RunAsync(sequence, [sequence], new Dictionary<string, object?>(), new Dictionary<string, object?>());
            if (await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10))) != runTask)
            {
                await host.StopAsync();
                throw new TimeoutException("OpenTAP 真实 CAN 序列超过 10 秒，已请求停止");
            }
            var run = await runTask;
            Console.WriteLine($"PCAN OpenTAP status={run.Result.Status} error={run.Result.Error} data={Convert.ToHexString(backend.LastData)} events={run.Events.Count}");
            foreach (var step in run.Result.Steps) Console.WriteLine($"PCAN step {step.StepId}: {step.Status} {step.Message}");
            Check(run.Result.Status == "completed" && backend.LastData.SequenceEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }),
                $"OpenTAP 自动序列通过真实 PCAN（{firstChannel} → {secondChannel}）");
            File.WriteAllText(Path.Combine(output, "summary.txt"), $"PASS\nTX={firstChannel}\nRX={secondChannel}\n");
            Console.WriteLine($"RESULT=PASS real CAN acceptance | {output}");
        }
        finally { await backend.DisconnectAsync(); await backend.DisposeAsync(); }
    }

    private sealed class RealCanAutomationBackend : IAutomationChannelBackend
    {
        private readonly ITransportSessionFactory _factory;
        private readonly string _txChannel;
        private readonly string _rxChannel;
        private ICanBusSession? _tx;
        private ICanBusSession? _rx;
        private Task<CanFrame>? _pendingFrame;
        private CancellationTokenSource? _receiveStop;
        public byte[] LastData { get; private set; } = [];
        public RealCanAutomationBackend(ITransportSessionFactory factory, string txChannel, string rxChannel)
        { _factory = factory; _txChannel = txChannel; _rxChannel = rxChannel; }
        public IReadOnlyList<string> ChannelIds => ["CAN.Tx", "CAN.Rx"];
        public bool IsAutomationActive { get; set; }
        public IReadOnlyList<SequenceChannelDefinition> GetChannels() =>
            [new("CAN.Tx", "integer[]", "真实 PCAN", false, true), new("CAN.Rx", "integer[]", "真实 PCAN", true, false)];
        public bool OwnsChannel(string channelId) => ChannelIds.Contains(channelId, StringComparer.OrdinalIgnoreCase);
        public Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken cancellationToken = default) => ConnectAsync(cancellationToken);
        public async Task EndAutomationAsync(CancellationToken cancellationToken = default) => await DisconnectAsync(cancellationToken);
        public async Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default)
        {
            if (!channelId.Equals("CAN.Rx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("CAN 通道只读支持 CAN.Rx");
            var receive = _pendingFrame ?? throw new IOException("尚未发送 CAN 测试请求");
            var frame = await receive.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
            _pendingFrame = null;
            LastData = frame.Data.ToArray();
            return JsonSerializer.SerializeToElement(LastData.Select(value => (int)value).ToArray());
        }
        public async Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default)
        {
            if (!channelId.Equals("CAN.Tx", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("CAN 通道只写支持 CAN.Tx");
            var tx = _tx ?? throw new IOException("PCAN 发送通道尚未打开");
            if (value.ValueKind != JsonValueKind.Array) throw new InvalidDataException("CAN 数据必须是数组");
            var data = value.EnumerateArray().Select(item => checked((byte)item.GetDouble())).ToArray();
            var rx = _rx ?? throw new IOException("PCAN 接收通道尚未打开");
            _receiveStop?.Dispose();
            _receiveStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _receiveStop.CancelAfter(TimeSpan.FromSeconds(3));
            // The session broadcasts only to active subscribers. Subscribe before
            // sending, otherwise a fast physical frame can arrive between steps.
            _pendingFrame = rx.ReceiveAsync(new CanFrameFilter(Id: 0x5A5), _receiveStop.Token).FirstAsync(_receiveStop.Token).AsTask();
            await tx.SendAsync(new CanFrame(0x5A5, data, Timestamp: DateTimeOffset.UtcNow), cancellationToken);
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => DisconnectCoreAsync(cancellationToken);
        private async Task ConnectAsync(CancellationToken token)
        {
            if (_tx is not null) return;
            var txResource = Resource("can-auto-tx", _txChannel);
            var rxResource = Resource("can-auto-rx", _rxChannel);
            _tx = _factory.CreateCan(txResource); _rx = _factory.CreateCan(rxResource);
            try
            {
                await _tx.OpenAsync(new(txResource.Id, txResource.Type, txResource.Settings, ResourceLeaseMode.SharedBus), token);
                await _rx.OpenAsync(new(rxResource.Id, rxResource.Type, rxResource.Settings, ResourceLeaseMode.SharedBus), token);
                IsAutomationActive = true;
            }
            catch { await DisconnectCoreAsync(token); throw; }
        }
        private async Task DisconnectCoreAsync(CancellationToken token)
        {
            IsAutomationActive = false;
            // Automation lease release and tool shutdown may overlap. Transfer
            // ownership before awaiting, so each native session is closed once.
            var stop = Interlocked.Exchange(ref _receiveStop, null);
            var pending = Interlocked.Exchange(ref _pendingFrame, null);
            var tx = Interlocked.Exchange(ref _tx, null);
            var rx = Interlocked.Exchange(ref _rx, null);
            stop?.Cancel();
            try
            {
                if (pending is not null) { try { await pending; } catch (OperationCanceledException) { } }
            }
            finally
            {
                stop?.Dispose();
                try { if (tx is not null) await tx.DisposeAsync(); }
                finally { if (rx is not null) await rx.DisposeAsync(); }
            }
        }
        public ValueTask DisposeAsync() => new(DisconnectCoreAsync(CancellationToken.None));
        private static TransportResource Resource(string id, string channel)
        {
            var resource = new TransportResource { Id = id, Name = id, Type = "can", LeaseMode = "shared_bus" };
            TransportSettingsAccessor.Write(resource, new(CanChannel: channel, CanBitrate: 500000, CanBackendId: "pcan"));
            return resource;
        }
    }

    private static async Task RunSerialEmulatorAsync(string portName, int baud, string logPath, CancellationToken token)
    {
        using var port = new System.IO.Ports.SerialPort(portName, baud, System.IO.Ports.Parity.None, 8, System.IO.Ports.StopBits.One)
        { NewLine = "\n", ReadTimeout = 250, WriteTimeout = 1500 };
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        using var log = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        await Task.Run(() => port.Open(), token);
        double voltage = 0, current = 1;
        var output = false;
        while (!token.IsCancellationRequested)
        {
            string command;
            try { command = port.ReadLine().Trim(); }
            catch (TimeoutException) { continue; }
            if (command.Length == 0) continue;
            log.WriteLine($"RX {command}");
            var normalized = command.TrimStart(':').ToUpperInvariant();
            string? response = normalized switch
            {
                "*IDN?" => "TestStation,RS485-SCPI-SIM,COM6,1.0",
                "SOUR:VOLT?" => voltage.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                "MEAS:VOLT?" => (output ? voltage : 0).ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                "SOUR:CURR?" => current.ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                "MEAS:CURR?" => (output ? Math.Min(current, voltage / 10.0) : 0).ToString("G17", System.Globalization.CultureInfo.InvariantCulture),
                "OUTP?" => output ? "1" : "0",
                "SYST:ERR?" => "0,\"No error\"",
                _ => null
            };
            if (normalized.StartsWith("SOUR:VOLT ", StringComparison.Ordinal)) double.TryParse(normalized[10..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out voltage);
            else if (normalized.StartsWith("SOUR:CURR ", StringComparison.Ordinal)) double.TryParse(normalized[10..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out current);
            else if (normalized.StartsWith("OUTP ", StringComparison.Ordinal)) output = normalized[5..] is "1" or "ON";
            if (response is not null) { port.Write(response + "\n"); log.WriteLine($"TX {response}"); }
        }
    }

    private static string Option(string[] args, string name, string fallback)
    {
        var index = Array.FindIndex(args, item => item.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }
    private static IEnumerable<T> FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var item in FindVisual<T>(VisualTreeHelper.GetChild(root, i))) yield return item;
    }
    private static void Capture(FrameworkElement element,string name)
    {
        element.UpdateLayout(); var bitmap=new RenderTargetBitmap((int)element.ActualWidth,(int)element.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(element);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file=File.Create(Path.Combine(_output,name+".png")); encoder.Save(file);
    }
    private sealed class Dialogs : ISequenceDialogService
    {
        public SequenceUnsavedChoice Choice { get; set; } = SequenceUnsavedChoice.Save;
        public Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name)=>Task.FromResult(Choice);
        public Task<bool> ConfirmDeleteSequenceAsync(string name)=>Task.FromResult(true);
        public Task<string?> RenameSequenceAsync(string name)=>Task.FromResult<string?>(name);
        public Task<bool> ConfirmStopExecutionAsync()=>Task.FromResult(true);
    }
}





