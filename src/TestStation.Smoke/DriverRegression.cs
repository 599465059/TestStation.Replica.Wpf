using System.IO;
using System.Text;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Drivers;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Resources;

namespace TestStation.Smoke;

internal static class DriverRegression
{
    public static async Task RunAsync()
    {
        var transport = new MemoryTransport("memory", async (request, token) =>
        {
            await Task.Delay(1, token);
            return new TransportFrame(DateTimeOffset.UtcNow, Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(request.Span) + ":OK"));
        });
        await transport.OpenAsync();
        var reply = await transport.TransactAsync(Encoding.ASCII.GetBytes("PING"), TimeSpan.FromSeconds(1));
        if (Encoding.ASCII.GetString(reply!.Payload) != "PING:OK") throw new Exception("Memory transport exchange failed.");
        transport.PublishEvent(new TransportFrame(DateTimeOffset.UtcNow, [0xAA], "event-1"));
        await using (var events = transport.ReadEventsAsync().GetAsyncEnumerator())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            if (!await events.MoveNextAsync().AsTask().WaitAsync(timeout.Token) || events.Current.Key != "event-1") throw new Exception("Memory transport event failed.");
        }
        await transport.DisposeAsync();

        var root = Path.Combine(Path.GetTempPath(), "test-station-drivers", Guid.NewGuid().ToString("N"));
        var workspace = ProjectWorkspace.Create(root, "drivers");
        var catalog = new ResourceCatalog
        {
            Transports = [new TransportResource { Id = "sim", Name = "模拟资源", Type = "custom" }],
            Devices = [new DeviceDefinition { Id = "supply", Name = "模拟电源", DriverId = "sim.psu", TransportId = "sim", Channels = [
                new ChannelDefinition { Id = "voltage", Name = "电压", Unit = "V", Polling = new() { Mode = "periodic", IntervalMs = 15, TimeoutMs = 100 } },
                new ChannelDefinition { Id = "status", Name = "状态", Access = "read" } ] }]
        };
        var repository = new ResourceCatalogRepository(); repository.Save(workspace.Root, catalog);
        var catalogService = new ResourceCatalogService(repository); catalogService.OpenProject(workspace.Root);
        var channels = new SimulatedChannelService(catalogService, new SimulatedDeviceDriverFactory());
        var updates = 0; channels.ValueUpdated += (_, _) => updates++;
        await channels.WriteAsync("supply", "voltage", 12.5);
        var reading = await channels.ReadAsync("supply", "voltage");
        if (reading.Value?.ToString() != "12.5") throw new Exception("Simulated channel write/read failed.");
        using var pollingCancellation = new CancellationTokenSource(100);
        try { await new PollingService(catalogService, channels).RunAsync("supply", pollingCancellation.Token); }
        catch (OperationCanceledException) { }
        if (updates < 3) throw new Exception("Periodic polling did not update the channel.");
        var lifecycle = new PollingService(catalogService, channels);
        if (!await lifecycle.StartAsync("supply") || !lifecycle.GetStatus("supply").IsRunning) throw new Exception("Polling lifecycle did not start.");
        await Task.Delay(45);
        if (!await lifecycle.StopAsync("supply") || lifecycle.GetStatus("supply").IsRunning) throw new Exception("Polling lifecycle did not stop.");
        try { await channels.WriteAsync("supply", "status", true); throw new Exception("Read-only channel accepted a write."); }
        catch (InvalidOperationException) { }
        channels.Dispose();
        Console.WriteLine("PASS: memory transport request/events, simulated device read/write, channel updates, polling and access rules");
    }
}
