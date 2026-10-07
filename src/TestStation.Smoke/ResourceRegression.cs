using System.IO;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Resources;
using TestStation.Application.Drivers;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Resources;

namespace TestStation.Smoke;

internal static class ResourceRegression
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "test-station-resources", Guid.NewGuid().ToString("N"));
        var workspace = ProjectWorkspace.Create(root, "resources");
        var catalog = new ResourceCatalog
        {
            Transports = [new TransportResource { Id = "psu-serial", Name = "电源串口", Type = "serial", Settings = new() { ["port"] = JsonDocument.Parse("\"COM3\"").RootElement, ["baud_rate"] = JsonDocument.Parse("9600").RootElement } }],
            Devices = [new DeviceDefinition
            {
                Id = "dc-supply", Name = "直流电源", DriverId = "vendor.psu", TransportId = "psu-serial", ProtocolId = "scpi",
                Channels = [new ChannelDefinition { Id = "voltage", Name = "输出电压", Unit = "V", Access = "read_write", Mapping = new() { ProtocolKind = "scpi", QueryTemplate = "MEAS:VOLT?", CommandTemplate = "VOLT {value}", Scale = 1, Offset = 0 }, Polling = new() { Mode = "periodic", IntervalMs = 200, TimeoutMs = 1000, RetryCount = 1 } }]
            }]
        };
        var repository = new ResourceCatalogRepository();
        repository.Save(workspace.Root, catalog);
        var loaded = repository.Load(workspace.Root);
        if (loaded.Errors.Count != 0 || loaded.Catalog.Devices.Single().Channels.Single().Polling.IntervalMs != 200 || loaded.Catalog.Devices.Single().Channels.Single().Mapping.ProtocolKind != "scpi")
            throw new Exception("Resource catalog round trip failed.");
        var service = new ResourceCatalogService(repository);
        service.OpenProject(workspace.Root);
        var editing = new ResourceCatalogEditingService(service);
        var addedTransport = editing.AddTransport("tcp");
        if (!service.Catalog.Transports.Contains(addedTransport)) throw new Exception("Resource editing service did not add transport.");
        var addedDevice = editing.AddDevice(addedTransport.Id);
        var addedChannel = editing.AddChannel(addedDevice.Id);
        editing.UpdateChannel(addedDevice.Id, addedChannel.Id, "反馈", "number", "V", "read");
        if (!editing.RemoveDevice(addedDevice.Id, out _)) throw new Exception("Resource editing service did not remove device.");
        if (!editing.RemoveTransport(addedTransport.Id, out _)) throw new Exception("Resource editing service did not remove unused transport.");
        if (editing.RemoveTransport("psu-serial", out var deleteError) || !deleteError.Contains("使用")) throw new Exception("Resource editing service allowed a referenced transport removal.");
        if (service.Catalog.Transports.Single().Settings["port"].GetString() != "COM3") throw new Exception("Resource settings were not restored.");
        var settings = TransportSettingsAccessor.Read(service.Catalog.Transports.Single());
        if (settings.BaudRate != 9600 || TransportSettingsAccessor.Describe("serial")["baud_rate"] != "波特率") throw new Exception("Typed transport settings were not read.");
        if (!TransportSettingsAccessor.TryApply(service.Catalog.Transports.Single(), new Dictionary<string, string> { ["baud_rate"] = "19200" }, out _) ||
            TransportSettingsAccessor.Read(service.Catalog.Transports.Single()).BaudRate != 19200) throw new Exception("Typed transport settings were not applied.");
        if (TransportSettingsAccessor.TryApply(service.Catalog.Transports.Single(), new Dictionary<string, string> { ["baud_rate"] = "invalid" }, out _) ) throw new Exception("Invalid transport settings were accepted.");
        var invalid = new ResourceCatalog { Transports = [new() { Id = "dup", Name = "A", Type = "serial" }, new() { Id = "dup", Name = "B", Type = "serial" }] };
        try { service.Save(invalid); throw new Exception("Invalid duplicate transport was accepted."); }
        catch (InvalidDataException) { }
        var driverCatalog = new BuiltInDriverCatalog();
        if (driverCatalog.Find("simulated") is null || driverCatalog.GetAll()[0].Protocols.Count == 0) throw new Exception("Driver catalog is empty.");
        Console.WriteLine("PASS: versioned resource catalog, editing boundary, typed transport settings, driver catalog, mapping, polling policy, validation and persistence");
    }
}
