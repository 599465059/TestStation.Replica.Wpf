using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Devices.HiokiPowerAnalyzer;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Scpi;

namespace TestStation.Infrastructure.Devices.HiokiPowerAnalyzer;

public sealed class HiokiPowerAnalyzerDeviceService : IHiokiPowerAnalyzerDeviceService, IAsyncDisposable
{
    private sealed record Active(IDeviceSession Session, IDisposable Lease, string Definition);
    private readonly IResourceCatalogService _catalog;
    private readonly IResourceManager _resources;
    private readonly ITransportSessionFactory _transports;
    private readonly ConcurrentDictionary<string, Active> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HiokiPowerAnalyzerConnection> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automationOwned = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _initializedRoot;
    private bool _initializing;
    private bool _disposed;
    private bool _familyEnabled = true;
    private volatile bool _automationActive;
    public const string FamilyId = HiokiPowerAnalyzerCatalog.FamilyId;
    public HiokiPowerAnalyzerDeviceService(IResourceCatalogService catalog, IResourceManager resources, ITransportSessionFactory transports)
    { _catalog = catalog; _resources = resources; _transports = transports; _catalog.Changed += CatalogChanged; _catalog.Changing += CatalogChanging; if (HasProject) CatalogChanged(this, EventArgs.Empty); }
    public IReadOnlyList<DeviceDefinition> Devices => _catalog.Catalog.Devices.Where(d => d.FamilyId == FamilyId).Select(Clone).ToArray();
    public IReadOnlyList<TransportResource> Resources => _catalog.Catalog.Transports.Select(Clone).ToArray();
    public bool HasProject => _catalog.ProjectRoot is not null;
    public bool FamilyEnabled { get => _familyEnabled; set { if (_familyEnabled == value) return; if (IsAutomationActive) throw new InvalidOperationException("自动测试期间不能修改设备家族"); _familyEnabled = value; if (!value) _ = DisconnectDisabledAsync(); Changed?.Invoke(this, EventArgs.Empty); } }
    public bool IsAutomationActive { get => _automationActive; set => _automationActive = value; }
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    public event EventHandler<ChannelRuntimeValue>? ValueChanged;
    public IReadOnlyList<string> ChannelIds => Devices.SelectMany(d => GetLocalChannels().Select(c => d.Id + "." + c.Id)).ToArray();
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => !FamilyEnabled ? [] : Devices.SelectMany(d => GetLocalChannels().Select(c => new SequenceChannelDefinition(d.Id + "." + c.Id, "number", d.Name + " / " + c.DisplayName, true, false))).ToArray();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
    private static IReadOnlyList<HiokiPowerAnalyzerChannel> GetLocalChannels() => HiokiPowerAnalyzerCatalog.Channels;
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private DeviceDefinition Find(string id) => Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"功率分析仪实例不存在：{id}");
    public bool OwnsChannel(string channelId) => Devices.Any(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase));
    public HiokiPowerAnalyzerConnection ConnectionFor(string deviceId) => _states.GetValueOrDefault(deviceId) ?? new();
    private void SetState(string id, HiokiPowerAnalyzerConnection state) { _states[id] = state; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void EnsureManual() { ObjectDisposedException.ThrowIf(_disposed, this); if (IsAutomationActive) throw new InvalidOperationException("自动测试期间不能修改设备或资源"); }
    private void SaveCatalog(ResourceCatalog catalog) { EnsureManual(); _catalog.Save(catalog); }
    public DeviceDefinition AddDevice(string name)
    {
        EnsureManual(); if (!HasProject) throw new InvalidOperationException("请先打开工程");
        var catalog = Clone(_catalog.Catalog); var i = 1; while (catalog.Devices.Any(d => d.Id.Equals($"PW6001{i:00}", StringComparison.OrdinalIgnoreCase))) i++;
        var device = new DeviceDefinition { Id = $"PW6001{i:00}", Name = Required(name), FamilyId = FamilyId, ModelId = "PW6001", DriverId = "hioki.pw6001", ProtocolId = "scpi", TransportId = "" };
        catalog.Devices.Add(device); SaveCatalog(catalog); return Clone(device);
    }
    public void UpdateDevice(string id, string name, string modelId, string resourceId)
    {
        EnsureManual(); if (ConnectionFor(id).Connected) throw new InvalidOperationException("请先断开设备");
        if (!modelId.Equals("PW6001", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PW6001 型号不可用");
        var catalog = Clone(_catalog.Catalog); var resource = string.IsNullOrWhiteSpace(resourceId) ? null : catalog.Transports.FirstOrDefault(r => r.Id == resourceId && r.Enabled);
        if (!string.IsNullOrWhiteSpace(resourceId) && resource is null) throw new InvalidOperationException("通信资源不可用");
        if (resource is not null && !TransportSettingsAccessor.NormalizeType(resource.Type).Equals("tcp", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("PW6001 首期仅支持 TCP");
        if (resource is not null && Devices.Any(d => d.Id != id && d.TransportId == resource.Id && ConnectionFor(d.Id).Connected)) throw new InvalidOperationException("通信资源已被其他已连接设备占用");
        var device = catalog.Devices.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException("设备实例不存在");
        device.Name = Required(name); device.ModelId = modelId; device.TransportId = resourceId; SaveCatalog(catalog);
    }
    public void RemoveDevice(string id) { EnsureManual(); if (ConnectionFor(id).Connected) throw new InvalidOperationException("请先断开设备"); var catalog = Clone(_catalog.Catalog); catalog.Devices.RemoveAll(d => d.Id == id); SaveCatalog(catalog); }
    public void SaveResource(TransportResource resource) { EnsureManual(); TransportSettingsAccessor.ValidateEndpoint(resource); if (!resource.Type.Equals("tcp", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("PW6001 首期仅支持 TCP"); var catalog = Clone(_catalog.Catalog); var index = catalog.Transports.FindIndex(r => r.Id == resource.Id); if (index >= 0) catalog.Transports[index] = Clone(resource); else catalog.Transports.Add(Clone(resource)); SaveCatalog(catalog); }
    public async Task ConnectAsync(string deviceId, CancellationToken token = default) { await _gate.WaitAsync(token); try { EnsureManual(); await ConnectCoreAsync(deviceId, token); } finally { _gate.Release(); } }
    private async Task ConnectCoreAsync(string deviceId, CancellationToken token)
    {
        if (_sessions.ContainsKey(deviceId)) return; if (!FamilyEnabled) throw new InvalidOperationException("HIOKI 家族已禁用"); var device = Find(deviceId); var resource = Resources.FirstOrDefault(r => r.Id == device.TransportId && r.Enabled) ?? throw new InvalidOperationException("设备尚未绑定可用 TCP 资源"); if (!TransportSettingsAccessor.NormalizeType(resource.Type).Equals("tcp", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("PW6001 首期仅支持 TCP"); TransportSettingsAccessor.ValidateEndpoint(resource);
        var lease = _resources.Acquire(resource.Id, "device:" + device.Id, ResourceLeaseMode.Exclusive); IDeviceSession? session = null;
        try { var transport = _transports.CreateByte(resource); var protocol = new ScpiProtocolSession(transport, new(resource.Id, resource.Type, resource.Settings)); var driver = new HiokiPowerAnalyzerDriver(device.Id, protocol); session = new HiokiPowerAnalyzerDeviceSession(device.Id, resource.Id, protocol, driver); await session.ConnectAsync(token); var identity = await protocol.QueryAsync("*IDN?", token); await protocol.SendAsync("*CLS", token); await protocol.SendAsync(":HEADer OFF", token); if (string.IsNullOrWhiteSpace(identity)) throw new InvalidDataException("设备身份响应为空"); _sessions[device.Id] = new(session, lease, JsonSerializer.Serialize(device)); SetState(device.Id, new(true, identity)); }
        catch (Exception error) { try { if (session is not null) await session.DisposeAsync(); } finally { lease.Dispose(); } SetState(device.Id, new(Error: error.Message)); throw; }
    }
    public async Task DisconnectDeviceAsync(string deviceId, CancellationToken token = default) { await _gate.WaitAsync(token); try { EnsureManual(); await CloseCoreAsync(deviceId); } finally { _gate.Release(); } }
    public async Task DisconnectAsync(CancellationToken token = default) { await _gate.WaitAsync(token); try { EnsureManual(); foreach (var id in _sessions.Keys.ToArray()) await CloseCoreAsync(id); } finally { _gate.Release(); } }
    private async Task CloseCoreAsync(string id) { if (_sessions.TryRemove(id, out var active)) { try { await active.Session.DisposeAsync(); } finally { active.Lease.Dispose(); } } SetState(id, new()); }
    private async Task DisconnectDisabledAsync() { await _gate.WaitAsync(); try { foreach (var id in _sessions.Keys.ToArray()) await CloseCoreAsync(id); } finally { _gate.Release(); } }
    private (string Device, HiokiPowerAnalyzerChannel Channel, IDeviceSession Session) Resolve(string channelId)
    {
        if (!FamilyEnabled) throw new InvalidOperationException("HIOKI 家族已禁用"); var device = Devices.FirstOrDefault(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"未知功率分析仪通道：{channelId}"); var local = channelId[(device.Id.Length + 1)..]; var channel = GetLocalChannels().FirstOrDefault(c => c.Id.Equals(local, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"通道不存在：{channelId}"); if (!_sessions.TryGetValue(device.Id, out var active)) throw new IOException("设备未连接"); return (device.Id, channel, active.Session);
    }
    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var (deviceId, _, session) = Resolve(channelId);
            try
            {
                var value = await session.ReadAsync(channelId, token);
                ValueChanged?.Invoke(this, new(channelId, value, DateTimeOffset.UtcNow));
                return value;
            }
            catch (Exception error) when (error is IOException or TimeoutException or InvalidDataException or FormatException)
            {
                if (_sessions.TryRemove(deviceId, out var active))
                {
                    try { await active.Session.DisposeAsync(); } finally { active.Lease.Dispose(); }
                }
                SetState(deviceId, new(Error: error.Message));
                throw;
            }
        }
        finally { _gate.Release(); }
    }
    public Task WriteAsync(string channelId, JsonElement value, CancellationToken token = default) => Task.FromException(new InvalidOperationException("PW6001 测量通道为只读"));
    public async Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken token = default) { await _gate.WaitAsync(token); var opened = new List<string>(); try { EnsureManual(); IsAutomationActive = true; foreach (var id in channels.Where(OwnsChannel).Select(c => c.Split('.', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase)) { if (_sessions.ContainsKey(id)) continue; await ConnectCoreAsync(id, token); opened.Add(id); } lock (_automationOwned) foreach (var id in opened) _automationOwned.Add(id); } catch { foreach (var id in opened) await CloseCoreAsync(id); IsAutomationActive = false; throw; } finally { _gate.Release(); } }
    public async Task EndAutomationAsync(CancellationToken token = default) { await _gate.WaitAsync(token); try { string[] owned; lock (_automationOwned) { owned = _automationOwned.ToArray(); _automationOwned.Clear(); } foreach (var id in owned) await CloseCoreAsync(id); IsAutomationActive = false; } finally { _gate.Release(); } }
    private void CatalogChanging(object? sender, ResourceCatalogChangingEventArgs args)
    {
        if (IsAutomationActive && (args.Root != _catalog.ProjectRoot || JsonSerializer.Serialize(args.Catalog) != JsonSerializer.Serialize(_catalog.Catalog)))
            throw new InvalidOperationException("自动测试期间不能修改功率分析仪设备资源");
        foreach (var pair in _sessions)
        {
            var next = args.Catalog.Devices.FirstOrDefault(d => d.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            if (args.Root != _catalog.ProjectRoot || next is null || JsonSerializer.Serialize(next) != pair.Value.Definition)
                throw new InvalidOperationException("已连接功率分析仪不能修改或删除，请先断开");
            var current = _catalog.Catalog.Devices.FirstOrDefault(d => d.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            var resourceId = current?.TransportId;
            if (!string.IsNullOrWhiteSpace(resourceId))
            {
                var oldResource = _catalog.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
                var newResource = args.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
                if (oldResource is null || newResource is null || JsonSerializer.Serialize(oldResource) != JsonSerializer.Serialize(newResource))
                    throw new InvalidOperationException("已连接功率分析仪的通信资源不能修改，请先断开");
            }
        }
    }
    private void CatalogChanged(object? sender, EventArgs e) { if (_initializing) return; if (HasProject && _initializedRoot != _catalog.ProjectRoot) { _initializing = true; try { _initializedRoot = _catalog.ProjectRoot; } finally { _initializing = false; } } Changed?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty); }
    private static string Required(string value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("名称不能为空") : value.Trim();
    public async ValueTask DisposeAsync() { await _gate.WaitAsync(); try { if (_disposed) return; _disposed = true; _catalog.Changed -= CatalogChanged; _catalog.Changing -= CatalogChanging; foreach (var id in _sessions.Keys.ToArray()) await CloseCoreAsync(id); } finally { _gate.Release(); } }
}

internal sealed class HiokiPowerAnalyzerDriver : IDeviceDriver
{
    private readonly IProtocolSession _protocol;
    private readonly object _cacheSync = new();
    private readonly Dictionary<int, (DateTimeOffset Timestamp, double[] Values)> _measurementCache = [];
    public HiokiPowerAnalyzerDriver(string deviceId, IProtocolSession protocol) { DeviceId = deviceId; _protocol = protocol; }
    public string DeviceId { get; }
    public string FamilyId => HiokiPowerAnalyzerCatalog.FamilyId;
    public string ModelId => "PW6001";
    public IReadOnlyList<SequenceChannelDefinition> Channels => HiokiPowerAnalyzerCatalog.Channels.Select(c => new SequenceChannelDefinition(c.Id, "number", c.DisplayName, true, false)).ToArray();
    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken token = default)
    {
        var local = channelId[(DeviceId.Length + 1)..]; var channel = HiokiPowerAnalyzerCatalog.Channels.FirstOrDefault(c => c.Id.Equals(local, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("PW6001 通道不存在");
        double[] values;
        lock (_cacheSync) if (_measurementCache.TryGetValue(channel.Channel, out var cached) && DateTimeOffset.UtcNow - cached.Timestamp < TimeSpan.FromMilliseconds(150)) values = cached.Values;
        else values = [];
        if (values.Length == 0)
        {
            values = HiokiPowerAnalyzerCatalog.Parse(await _protocol.QueryAsync(HiokiPowerAnalyzerCatalog.Query(channel.Channel), token));
            lock (_cacheSync) _measurementCache[channel.Channel] = (DateTimeOffset.UtcNow, values);
        }
        return JsonSerializer.SerializeToElement(values[channel.ValueIndex]);
    }
    public Task WriteAsync(string channelId, JsonElement value, CancellationToken token = default) => Task.FromException(new InvalidOperationException("PW6001 测量通道为只读"));
    public ValueTask DisposeAsync() { lock (_cacheSync) _measurementCache.Clear(); return _protocol.DisposeAsync(); }
}

internal sealed class HiokiPowerAnalyzerDeviceSession : IDeviceSession
{
    private readonly IProtocolSession _protocol; private readonly IDeviceDriver _driver; private bool _disposed;
    public HiokiPowerAnalyzerDeviceSession(string deviceId, string resourceId, IProtocolSession protocol, IDeviceDriver driver) { DeviceId = deviceId; ResourceId = resourceId; _protocol = protocol; _driver = driver; }
    public string DeviceId { get; } public string ResourceId { get; } public bool IsConnected { get; private set; }
    public async Task ConnectAsync(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed, this); await _protocol.OpenAsync(token); IsConnected = true; }
    public async Task DisconnectAsync(CancellationToken token = default) { if (!IsConnected) return; await _protocol.CloseAsync(token); IsConnected = false; }
    public Task<JsonElement> ReadAsync(string channelId, CancellationToken token = default) { if (!IsConnected) throw new IOException("设备未连接"); return _driver.ReadAsync(channelId, token); }
    public Task WriteAsync(string channelId, JsonElement value, CancellationToken token = default) => Task.FromException(new InvalidOperationException("PW6001 测量通道为只读"));
    public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await _driver.DisposeAsync(); IsConnected = false; }
}
