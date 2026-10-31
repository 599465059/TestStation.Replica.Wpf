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
            try { await Run(root); }
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
    private static async Task Run(string root)
    {
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





