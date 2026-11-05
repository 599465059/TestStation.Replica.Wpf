using System.Collections.Concurrent;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Devices;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Devices.Reference;

/// <summary>Runs the reference project's implemented SCPI families through TestStation sessions.</summary>
public sealed class ReferenceScpiDeviceService : IInstrumentDeviceService, IAsyncDisposable
{
    private sealed record Active(InstrumentConnection Connection, IDisposable Lease, string Definition);
    private readonly IResourceCatalogService _catalog;
    private readonly IResourceManager _resources;
    private readonly ITransportSessionFactory _transports;
    private readonly ReferenceScpiCatalog _definitions = new();
    private readonly Dictionary<string, ScpiFamilyData> _familyData;
    private readonly Dictionary<string, InstrumentFamily> _familiesById;
    private readonly ConcurrentDictionary<string, Active> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManagedDeviceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automationOwned = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _automation;
    private bool _disposed;
    private readonly ConcurrentDictionary<string, byte> _disabled = new(StringComparer.OrdinalIgnoreCase);
    public ReferenceScpiDeviceService(IResourceCatalogService catalog, IResourceManager resources, ITransportSessionFactory transports)
    {
        _catalog = catalog; _resources = resources; _transports = transports;
        _familyData = _definitions.Families.ToDictionary(f => f.FamilyId, StringComparer.OrdinalIgnoreCase);
        _familiesById = _definitions.Families.ToDictionary(f => f.FamilyId, _definitions.ToDefinition, StringComparer.OrdinalIgnoreCase);
        Families = _familiesById.Values.Select(f => (IManagedDeviceFamily)new FamilyAdapter(this, f)).ToArray();
        _catalog.Changed += CatalogChanged;
    }
    public IReadOnlyList<InstrumentFamily> Definitions => _familiesById.Values.ToArray();
    public IReadOnlyList<IManagedDeviceFamily> Families { get; }
    public IReadOnlyList<DeviceDefinition> Devices => _catalog.Catalog.Devices.Where(d => _familyData.ContainsKey(d.FamilyId)).Select(Clone).ToArray();
    public IReadOnlyList<string> ChannelIds => GetChannels().Select(c => c.Id).ToArray();
    public bool IsAutomationActive { get => _automation; set => _automation = value; }
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    public event EventHandler<InstrumentTraffic>? Traffic;
    public bool OwnsChannel(string channelId) => Devices.Any(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => Devices.Where(d => !_disabled.ContainsKey(d.FamilyId)).SelectMany(d =>
    {
        var model = ModelFor(d.Id); return (model.RuntimeAvailable ? model.Channels : []).Select(c => new SequenceChannelDefinition(d.Id + "." + c.Id, c.DataType, d.Name + " / " + c.Name, c.CanRead, c.CanWrite));
    }).ToArray();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
    public ManagedDeviceState StateFor(string id) => _states.GetValueOrDefault(id, new(false));
    public InstrumentModel ModelFor(string deviceId)
    {
        var device = Devices.FirstOrDefault(d => d.Id.Equals(deviceId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"设备实例不存在：{deviceId}");
        return _familiesById[device.FamilyId].Models.FirstOrDefault(m => m.Id.Equals(device.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"型号不存在：{device.ModelId}");
    }
    public Task SetFamilyEnabledAsync(string familyId, bool enabled, CancellationToken token = default) => WithGateAsync(async () =>
    {
        if (_disabled.ContainsKey(familyId) == !enabled) return;
        EnsureManual();
        if (enabled) _disabled.TryRemove(familyId, out _);
        else
        {
            _disabled[familyId] = 0;
            await DisconnectManyAsync(Devices.Where(d => d.FamilyId.Equals(familyId, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id)).ConfigureAwait(false);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }, token);
    internal void EnsureFamily(string id) { if (!_familyData.ContainsKey(id)) throw new InvalidOperationException($"设备家族未接入：{id}"); if (_disabled.ContainsKey(id)) throw new InvalidOperationException("设备家族已禁用"); }
    internal DeviceDefinition Add(string familyId, string name)
    {
        EnsureFamily(familyId); EnsureManual(); if (_catalog.ProjectRoot is null) throw new InvalidOperationException("请先打开工程");
        var family = _familiesById[familyId]; var model = family.Models.First(); var catalog = Clone(_catalog.Catalog); var n = 1; string id;
        do id = $"{family.Name.Replace(' ', '-').ToLowerInvariant()}-{n++:00}"; while (catalog.Devices.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        var created = new DeviceDefinition { Id = id, Name = string.IsNullOrWhiteSpace(name) ? family.Name : name.Trim(), FamilyId = familyId, ModelId = model.Id, DriverId = familyId, ProtocolId = "scpi" };
        catalog.Devices.Add(created); _catalog.Save(catalog); return Clone(created);
    }
    internal void Update(string id, string name, string modelId, string resourceId)
    {
        EnsureManual(); var device = Find(id); EnsureFamily(device.FamilyId); if (StateFor(id).Connected) throw new InvalidOperationException("请先断开设备");
        var model = _familiesById[device.FamilyId].Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("型号不可用");
        var catalog = Clone(_catalog.Catalog); var item = catalog.Devices.First(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var resource = string.IsNullOrWhiteSpace(resourceId) ? null : catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase) && r.Enabled);
        if (!string.IsNullOrWhiteSpace(resourceId) && resource is null) throw new InvalidOperationException("通信资源不可用");
        if (resource is not null && TransportSettingsAccessor.NormalizeType(resource.Type) is not ("tcp" or "serial")) throw new NotSupportedException("参考 SCPI 设备首期仅支持 TCP 和串口");
        item.Name = name.Trim(); item.ModelId = model.Id; item.TransportId = resourceId ?? ""; _catalog.Save(catalog);
    }
    internal void Remove(string id) { EnsureManual(); if (StateFor(id).Connected) throw new InvalidOperationException("请先断开设备"); var catalog = Clone(_catalog.Catalog); catalog.Devices.RemoveAll(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); _catalog.Save(catalog); }
    internal Task ConnectAsync(string id, CancellationToken token) => WithGateAsync(() => { EnsureManual(); return ConnectCoreAsync(id, token); }, token);
    internal Task DisconnectAsync(string id, CancellationToken token) => WithGateAsync(() => { EnsureManual(); return DisconnectCoreAsync(id); }, token);
    private async Task ConnectCoreAsync(string id, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); var device = Find(id); EnsureFamily(device.FamilyId); if (_sessions.ContainsKey(id)) return;
        var resource = _catalog.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(device.TransportId, StringComparison.OrdinalIgnoreCase) && r.Enabled) ?? throw new InvalidOperationException("设备尚未绑定可用通信资源");
        var type = TransportSettingsAccessor.NormalizeType(resource.Type); if (type is not ("tcp" or "serial")) throw new NotSupportedException("SCPI 设备仅支持 TCP 和串口");
        var model = ModelFor(id); var familyData = _familyData[device.FamilyId]; var modelData = familyData.Models.First(m => m.Id.Equals(model.Id, StringComparison.OrdinalIgnoreCase));
        var lease = _resources.Acquire(resource.Id, "device:" + id, ResourceLeaseMode.Exclusive); InstrumentConnection? connection = null;
        try
        {
            var parameters = new Dictionary<string, (ScpiParameter, int)>(StringComparer.OrdinalIgnoreCase); var actions = new Dictionary<string, (ScpiAction, int)>(StringComparer.OrdinalIgnoreCase); var count = Math.Max(1, modelData.Definition.ChannelCount);
            for (var ch = 1; ch <= count; ch++) { var prefix = count == 1 ? "" : $"CH{ch}."; foreach (var p in modelData.Definition.Parameters) parameters[prefix + p.Name] = (p, ch); foreach (var a in modelData.Definition.Commands.Where(a => a.IsEnabled)) actions[prefix + "action." + a.Name] = (a, ch); }
            connection = new ScpiInstrumentConnection(modelData.Definition, _transports.CreateByte(resource), resource, parameters, actions, (direction, message) => Traffic?.Invoke(this, new(DateTimeOffset.UtcNow, id, direction, message)));
            await connection.OpenAsync(token).ConfigureAwait(false); _sessions[id] = new(connection, lease, JsonSerializer.Serialize(device)); _states[id] = new(true); StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception error)
        {
            try { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["ConnectionCleanup"] = cleanup.ToString(); }
            finally { lease.Dispose(); _states[id] = new(false, error.Message); StateChanged?.Invoke(this, EventArgs.Empty); }
            throw;
        }
    }
    private async Task DisconnectCoreAsync(string id)
    {
        try
        {
            if (_sessions.TryRemove(id, out var active))
                try { await active.Connection.DisposeAsync().ConfigureAwait(false); }
                finally { active.Lease.Dispose(); }
        }
        finally { _states[id] = new(false); StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    private async Task DisconnectManyAsync(IEnumerable<string> ids)
    {
        var errors = new List<Exception>();
        foreach (var id in ids.ToArray())
            try { await DisconnectCoreAsync(id).ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("部分设备断开失败，已继续释放其余设备", errors);
    }
    private void CatalogChanged(object? sender, EventArgs args)
    { Changed?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty); }
    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (id, local) = Resolve(channelId);
            var channel = InstrumentChannelValidation.Find(ModelFor(id), local);
            if (!channel.CanRead) throw new InvalidOperationException("通道不可读");
            if (!_sessions.TryGetValue(id, out var active)) throw new IOException("设备未连接");
            try
            {
                var value = await active.Connection.ReadAsync(local, token).ConfigureAwait(false);
                try { InstrumentChannelValidation.ValidateValue(channel, value); }
                catch (ArgumentException error) { throw new InvalidDataException("设备响应类型或范围不符合通道定义", error); }
                return value;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
            { await FaultAsync(id, error).ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    public async Task WriteAsync(string channelId, JsonElement value, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (id, local) = Resolve(channelId);
            var channel = InstrumentChannelValidation.Find(ModelFor(id), local);
            if (!channel.CanWrite) throw new InvalidOperationException("通道不可写");
            InstrumentChannelValidation.ValidateValue(channel, value);
            if (!_sessions.TryGetValue(id, out var active)) throw new IOException("设备未连接");
            try { await active.Connection.WriteAsync(local, value, token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
            { await FaultAsync(id, error).ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    private async Task FaultAsync(string id, Exception error)
    {
        try { await DisconnectCoreAsync(id).ConfigureAwait(false); }
        catch (Exception cleanup) { error.Data["ConnectionCleanup"] = cleanup.ToString(); }
        finally { _states[id] = new(false, error.Message); StateChanged?.Invoke(this, EventArgs.Empty); }
    }
    private (string Device, string Local) Resolve(string channel)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var device = Devices.Where(d => channel.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Id.Length).FirstOrDefault() ?? throw new InvalidOperationException($"设备通道不存在：{channel}");
        EnsureFamily(device.FamilyId);
        return (device.Id, channel[(device.Id.Length + 1)..]);
    }
    public async Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var opened = new List<string>();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var requested = channels.Select(Resolve).ToArray();
            foreach (var item in requested) InstrumentChannelValidation.Find(ModelFor(item.Device), item.Local);
            _automation = true;
            foreach (var id in requested.Select(c => c.Device).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_sessions.ContainsKey(id)) continue;
                await ConnectCoreAsync(id, token).ConfigureAwait(false);
                opened.Add(id);
            }
            foreach (var id in opened) _automationOwned.Add(id);
        }
        catch (Exception error)
        {
            try { await DisconnectManyAsync(opened).ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["AutomationRollback"] = cleanup.ToString(); }
            _automation = false;
            throw;
        }
        finally { _gate.Release(); }
    }
    public async Task EndAutomationAsync(CancellationToken token = default)
    {
        // Cancellation of a sequence must not cancel cleanup.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var ids = _automationOwned.ToArray();
            _automationOwned.Clear();
            await DisconnectManyAsync(ids).ConfigureAwait(false);
        }
        finally { _automation = false; _gate.Release(); }
    }
    public async Task DisconnectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectManyAsync(_sessions.Keys).ConfigureAwait(false); }
        finally { _automationOwned.Clear(); _automation = false; _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _catalog.Changed -= CatalogChanged;
        try { await DisconnectAsync().ConfigureAwait(false); }
        finally { _gate.Dispose(); }
    }
    private void EnsureManual() { ObjectDisposedException.ThrowIf(_disposed, this); if (_automation) throw new InvalidOperationException("自动测试期间不能修改设备"); }
    private async Task WithGateAsync(Func<Task> action, CancellationToken token) { await _gate.WaitAsync(token).ConfigureAwait(false); try { await action().ConfigureAwait(false); } finally { _gate.Release(); } }
    private DeviceDefinition Find(string id) => Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"设备实例不存在：{id}");
    private static DeviceDefinition Clone(DeviceDefinition source) => JsonSerializer.Deserialize<DeviceDefinition>(JsonSerializer.Serialize(source))!;
    private static ResourceCatalog Clone(ResourceCatalog source) => JsonSerializer.Deserialize<ResourceCatalog>(JsonSerializer.Serialize(source))!;

    private sealed class FamilyAdapter(ReferenceScpiDeviceService owner, InstrumentFamily definition) : IManagedDeviceFamily
    {
        public string Id => definition.Id; public string Name => definition.Name; public bool Enabled => !owner._disabled.ContainsKey(Id);
        public IReadOnlyList<ManagedDeviceModel> Models => definition.Models.Select(m => new ManagedDeviceModel(m.Id, m.Name, m.Transports)).ToArray();
        public event EventHandler? Changed { add => owner.Changed += value; remove => owner.Changed -= value; }
        public ManagedDeviceState GetState(string deviceId) => owner.StateFor(deviceId);
        public DeviceDefinition Add(string name) => owner.Add(Id, name);
        public void Update(string id, string name, string modelId, string resourceId) => owner.Update(id, name, modelId, resourceId);
        public void Remove(string id) => owner.Remove(id);
        public Task ConnectAsync(string id, CancellationToken token = default) => owner.ConnectAsync(id, token);
        public Task DisconnectAsync(string id, CancellationToken token = default) => owner.DisconnectAsync(id, token);
        public Task DisconnectAllAsync(CancellationToken token = default) => owner.DisconnectFamilyAsync(Id, token);
    }
    private Task DisconnectFamilyAsync(string familyId, CancellationToken token) => WithGateAsync(async () => { EnsureManual(); foreach (var id in Devices.Where(d => d.FamilyId.Equals(familyId, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToArray()) await DisconnectCoreAsync(id).ConfigureAwait(false); }, token);
}
