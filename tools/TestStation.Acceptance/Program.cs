using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace TestStation.Acceptance;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".");
        if (!File.Exists(Path.Combine(root, "TestStation.slnx"))) throw new ArgumentException("请提供软件源码工程根目录");
        var output = Path.Combine(root, "artifacts", "automatic-acceptance", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        var sample = AcceptanceProject.SamplePath(root);
        if (args.Contains("--generate") || !File.Exists(Path.Combine(sample, "project.json"))) sample = AcceptanceProject.Create(root);
        if (args.Contains("--generate-only")) { Console.WriteLine(sample); return 0; }
        Environment.SetEnvironmentVariable("TESTSTATION_OPENTAP_HOST", Path.Combine(root, "src", "TestStation.App", "bin", "Debug", "net10.0-windows", "OpenTapHost", "TestStation.OpenTapHost.dll"));
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        TestStation.App.Themes.ResourceBootstrap.Initialize(app);
        var suite = new AcceptanceSuite(sample, output);
        var exitCode = 0;
        app.Startup += async (_, _) =>
        {
            var clock = Stopwatch.StartNew();
            try { await suite.RunAsync(args.Contains("--follow-only")); }
            catch (Exception error) { exitCode = 1; suite.RecordFailure(error); Console.Error.WriteLine(error); }
            finally
            {
                File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
                { passed = exitCode == 0, elapsed_seconds = clock.Elapsed.TotalSeconds, sample, checks = suite.Checks }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"RESULT={(exitCode == 0 ? "PASS" : "FAIL")} | {suite.Checks.Count} checks | {clock.Elapsed.TotalSeconds:F1}s | {output}");
                app.Shutdown(exitCode);
            }
        };
        app.Run();
        return exitCode;
    }
}
