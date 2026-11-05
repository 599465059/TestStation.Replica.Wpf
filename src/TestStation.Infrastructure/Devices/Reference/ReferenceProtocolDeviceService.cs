using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Devices;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Devices;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Itech;
using TestStation.Infrastructure.Protocols.Modbus;
using TestStation.Infrastructure.Protocols.Scpi;
using TestStation.Infrastructure.Protocols.Bp7330;
using TestStation.Infrastructure.Protocols.HZP;

namespace TestStation.Infrastructure.Devices.Reference;

/// <summary>
/// Device service for reference-project B/C families. Each family retains its
/// protocol ID and transport requirements; this service only supplies shared
/// instance/session routing and never converts binary devices into SCPI.
/// </summary>
public sealed class ReferenceProtocolDeviceService : IInstrumentDeviceService, IAsyncDisposable
{
    private sealed record Active(ProtocolConnection Connection, IDisposable Lease);
    private readonly IResourceCatalogService _catalog;
    private readonly IResourceManager _resources;
    private readonly ITransportSessionFactory _transports;
    private readonly Dictionary<string, InstrumentFamily> _families;
    private readonly ConcurrentDictionary<string, Active> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ManagedDeviceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _automationOwned = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _automation;
    private bool _disposed;

    public ReferenceProtocolDeviceService(IResourceCatalogService catalog, IResourceManager resources, ITransportSessionFactory transports)
    {
        _catalog = catalog; _resources = resources; _transports = transports;
        _families = NonScpiDeviceCatalog.Families.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        Families = _families.Values.Select(f => (IManagedDeviceFamily)new FamilyAdapter(this, f)).ToArray();
        _catalog.Changed += CatalogChanged;
    }
    public IReadOnlyList<InstrumentFamily> Definitions => _families.Values.ToArray();
    public IReadOnlyList<IManagedDeviceFamily> Families { get; }
    public IReadOnlyList<DeviceDefinition> Devices => _catalog.Catalog.Devices.Where(d => _families.ContainsKey(d.FamilyId)).Select(Clone).ToArray();
    public IReadOnlyList<string> ChannelIds => GetChannels().Select(c => c.Id).ToArray();
    public bool IsAutomationActive { get => _automation; set => _automation = value; }
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    public event EventHandler<InstrumentTraffic>? Traffic;
    public bool OwnsChannel(string channelId) => Devices.Any(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase));
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => Devices.Where(d => !_disabled.ContainsKey(d.FamilyId)).SelectMany(d => (ModelFor(d.Id).RuntimeAvailable ? ModelFor(d.Id).Channels : []).Select(c => new SequenceChannelDefinition(d.Id + "." + c.Id, c.DataType, d.Name + " / " + c.Name, c.CanRead, c.CanWrite))).ToArray();
    public IReadOnlyDictionary<string, JsonElement> GetDefaultValues() => new Dictionary<string, JsonElement>();
    public ManagedDeviceState StateFor(string id) => _states.GetValueOrDefault(id, new(false));
    public InstrumentModel ModelFor(string deviceId)
    {
        var d = Find(deviceId); return _families[d.FamilyId].Models.FirstOrDefault(m => m.Id.Equals(d.ModelId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("设备型号不存在");
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
    internal DeviceDefinition Add(string familyId, string name)
    {
        EnsureFamily(familyId); EnsureManual(); if (_catalog.ProjectRoot is null) throw new InvalidOperationException("请先打开工程");
        var data = Clone(_catalog.Catalog); var i = 1; string id; do id = familyId.Replace('.', '-') + $"-{i++:00}"; while (data.Devices.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        var family = _families[familyId]; var model = family.Models[0]; var created = new DeviceDefinition { Id = id, Name = string.IsNullOrWhiteSpace(name) ? family.Name : name.Trim(), FamilyId = familyId, ModelId = model.Id, DriverId = familyId, ProtocolId = model.Protocol };
        data.Devices.Add(created); _catalog.Save(data); return Clone(created);
    }
    internal void Update(string id, string name, string modelId, string resourceId)
    {
        EnsureManual(); var d = Find(id); EnsureFamily(d.FamilyId); if (StateFor(id).Connected) throw new InvalidOperationException("请先断开设备"); var model = ModelForFamily(d.FamilyId, modelId); var data = Clone(_catalog.Catalog);
        var item = data.Devices.First(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); var resource = string.IsNullOrWhiteSpace(resourceId) ? null : data.Transports.FirstOrDefault(x => x.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase) && x.Enabled);
        if (!string.IsNullOrWhiteSpace(resourceId) && resource is null) throw new InvalidOperationException("通信资源不可用");
        if (resource is not null && !model.Transports.Contains(TransportSettingsAccessor.NormalizeType(resource.Type), StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException($"{model.Protocol} 不支持资源 {resource.Type}");
        item.Name = name.Trim(); item.ModelId = model.Id; item.TransportId = resourceId ?? ""; item.ProtocolId = model.Protocol; _catalog.Save(data);
    }
    internal void Remove(string id) { EnsureManual(); if (StateFor(id).Connected) throw new InvalidOperationException("请先断开设备"); var data = Clone(_catalog.Catalog); data.Devices.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); _catalog.Save(data); }
    internal Task ConnectAsync(string id, CancellationToken token) => WithGateAsync(() => { EnsureManual(); return ConnectCoreAsync(id, token); }, token);
    internal Task DisconnectAsync(string id, CancellationToken token) => WithGateAsync(() => { EnsureManual(); return DisconnectCoreAsync(id); }, token);
    private async Task ConnectCoreAsync(string id, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); var d = Find(id); EnsureFamily(d.FamilyId); if (_sessions.ContainsKey(id)) return; var resource = _catalog.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(d.TransportId, StringComparison.OrdinalIgnoreCase) && r.Enabled) ?? throw new InvalidOperationException("设备尚未绑定可用通信资源");
        var model = ModelFor(id); if (!model.RuntimeAvailable) throw new NotSupportedException($"{model.Name}：{model.Validation}"); if (!model.Transports.Contains(TransportSettingsAccessor.NormalizeType(resource.Type), StringComparer.OrdinalIgnoreCase)) throw new NotSupportedException($"{model.Protocol} 不支持资源 {resource.Type}");
        var leaseMode = model.Protocol == "zlg-can" ? ResourceLeaseMode.SharedBus : ResourceLeaseMode.Exclusive; var lease = _resources.Acquire(resource.Id, "device:" + id, leaseMode); ProtocolConnection? connection = null;
        try { connection = new ProtocolConnection(d, model, resource, _transports); await connection.OpenAsync(token).ConfigureAwait(false); _sessions[id] = new(connection, lease); _states[id] = new(true); StateChanged?.Invoke(this, EventArgs.Empty); }
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
                Traffic?.Invoke(this, new(DateTimeOffset.UtcNow, id, "RX", local));
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
    private InstrumentModel ModelForFamily(string familyId, string modelId) => _families[familyId].Models.FirstOrDefault(m => m.Id.Equals(modelId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("设备型号不存在");
    private void EnsureFamily(string id) { if (!_families.ContainsKey(id)) throw new InvalidOperationException("设备家族未接入"); if (_disabled.ContainsKey(id)) throw new InvalidOperationException("设备家族已禁用"); }
    private static DeviceDefinition Clone(DeviceDefinition d) => JsonSerializer.Deserialize<DeviceDefinition>(JsonSerializer.Serialize(d))!;
    private static ResourceCatalog Clone(ResourceCatalog d) => JsonSerializer.Deserialize<ResourceCatalog>(JsonSerializer.Serialize(d))!;

    private sealed class FamilyAdapter(ReferenceProtocolDeviceService owner, InstrumentFamily definition) : IManagedDeviceFamily
    {
        public string Id => definition.Id; public string Name => definition.Name; public bool Enabled => !owner._disabled.ContainsKey(Id); public IReadOnlyList<ManagedDeviceModel> Models => definition.Models.Select(m => new ManagedDeviceModel(m.Id, m.Name, m.Transports)).ToArray(); public event EventHandler? Changed { add => owner.Changed += value; remove => owner.Changed -= value; } public ManagedDeviceState GetState(string id) => owner.StateFor(id); public DeviceDefinition Add(string name) => owner.Add(Id, name); public void Update(string id, string name, string modelId, string resourceId) => owner.Update(id, name, modelId, resourceId); public void Remove(string id) => owner.Remove(id); public Task ConnectAsync(string id, CancellationToken token = default) => owner.ConnectAsync(id, token); public Task DisconnectAsync(string id, CancellationToken token = default) => owner.DisconnectAsync(id, token); public Task DisconnectAllAsync(CancellationToken token = default) => owner.WithGateAsync(async () => { owner.EnsureManual(); foreach (var id in owner.Devices.Where(d => d.FamilyId.Equals(Id, StringComparison.OrdinalIgnoreCase)).Select(d => d.Id).ToArray()) await owner.DisconnectCoreAsync(id).ConfigureAwait(false); }, token);
    }

    private sealed class ProtocolConnection : IAsyncDisposable
    {
        private readonly DeviceDefinition _device; private readonly InstrumentModel _model; private readonly TransportResource _resource; private readonly ITransportSessionFactory _factory; private IByteTransportSession? _byte; private ModbusProtocolSession? _modbus; private ItechFixedFrameProtocolSession? _itech; private ScpiProtocolSession? _scpi; private Bp7330Protocol? _bp; private HzpProtocolSession? _hzp;
        public ProtocolConnection(DeviceDefinition device, InstrumentModel model, TransportResource resource, ITransportSessionFactory factory) { _device = device; _model = model; _resource = resource; _factory = factory; }
        public async Task OpenAsync(CancellationToken token)
        {
            if (_model.Protocol is "ni-daqmx" or "relay-sdk" or "zlg-can") throw new NotSupportedException($"{_model.Name} 需要安装并注册厂商 SDK，当前未伪装为模拟设备");
            _byte = _factory.CreateByte(_resource);
            if (_model.Protocol is "modbus" or "modbus-sdk" or "scpi-modbus") { _modbus = new ModbusProtocolSession(_byte, _resource); await _modbus.OpenAsync(token).ConfigureAwait(false); }
            else if (_model.Protocol == "itech-fixed") { _itech = new ItechFixedFrameProtocolSession(_byte, _resource); await _itech.OpenAsync(token).ConfigureAwait(false); }
            else if (_model.Protocol == "bp7330") { _bp = new Bp7330Protocol(_byte, _resource); await _bp.OpenAsync(token).ConfigureAwait(false); }
            else if (_model.Protocol == "hzp") { _hzp = new HzpProtocolSession(_byte, _resource); await _hzp.OpenAsync(token).ConfigureAwait(false); }
            else if (_model.Protocol is "scpi" or "n39240-udp-scpi") { var endpoint = new TransportEndpoint(_resource.Id, _resource.Type, new Dictionary<string, JsonElement>(_resource.Settings, StringComparer.OrdinalIgnoreCase) { ["terminator"] = JsonSerializer.SerializeToElement("\n") }); _scpi = new ScpiProtocolSession(_byte, endpoint); await _scpi.OpenAsync(token).ConfigureAwait(false); }
            else
            {
                // Dedicated raw protocols have a transport session but must
                // provide their own frame map before a channel can be used.
                // Do not route them through SCPI by accident.
                var settings = new Dictionary<string, JsonElement>(_resource.Settings, StringComparer.OrdinalIgnoreCase) { ["terminator"] = JsonSerializer.SerializeToElement("") };
                await _byte.OpenAsync(new(_resource.Id, _resource.Type, settings), token).ConfigureAwait(false);
            }
        }
        public async Task<JsonElement> ReadAsync(string channel, CancellationToken token)
        {
            if (_modbus is not null) { var address = ParseAddress(channel); var values = channel.StartsWith("input", StringComparison.OrdinalIgnoreCase) ? await _modbus.ReadInputAsync(address, 1, token).ConfigureAwait(false) : await _modbus.ReadHoldingAsync(address, 1, token).ConfigureAwait(false); return JsonSerializer.SerializeToElement(values[0]); }
            if (_itech is not null) { var frame = await _itech.ReadMeasurementAsync(_model.Name.Contains("IT8500", StringComparison.OrdinalIgnoreCase), token).ConfigureAwait(false); return channel switch { "ReadVoltage" => JsonSerializer.SerializeToElement(ReadU32(frame, _model.Name.Contains("IT8500") ? 3 : 5) / 1000d), "ReadCurrent" => JsonSerializer.SerializeToElement((_model.Name.Contains("IT8500") ? ReadU32(frame, 7) : BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(3))) / 1000d), "ReadPower" => JsonSerializer.SerializeToElement(ReadU32(frame, 11) / 1000d), "OutputStatus" => JsonSerializer.SerializeToElement((_model.Name.Contains("IT8500") ? frame[15] : frame[10]) is var status && (status & 1) != 0), _ => throw new InvalidOperationException("ITECH 固定帧通道不可读") }; }
            if (_bp is not null) { var address = channel switch { "Phase1Voltage" => (ushort)0x1780, "Phase1Current" => (ushort)0x1783, "OutputEnabled" => (ushort)0x1776, _ => (ushort)0x177E }; var value = await _bp.ReadRegisterAsync(address, token).ConfigureAwait(false); return channel == "OutputEnabled" ? JsonSerializer.SerializeToElement(value == 2) : JsonSerializer.SerializeToElement(value / 10d); }
            if (_hzp is not null) { var values = await _hzp.ReadMeasurementsAsync(token).ConfigureAwait(false); return channel == "Energy" ? JsonSerializer.SerializeToElement((double)values[6]) : JsonSerializer.SerializeToElement((double)values[0]); }
            if (_scpi is not null) { var query = channel switch { "ReadVoltage" => "MEASure:VOLTage?", "ReadCurrent" => "MEASure:CURRent?", "OutputStatus" => "OUTPut?", "Temperature" => "MEASure:TEMPerature?", _ => "MEASure?" }; return ParseText(await _scpi.QueryAsync(query, token).ConfigureAwait(false)); }
            throw new NotSupportedException($"{_model.Protocol} 尚未配置通道帧映射；不会使用 SCPI 或内存值替代");
        }
        public async Task WriteAsync(string channel, JsonElement value, CancellationToken token)
        {
            if (_modbus is not null) { await _modbus.WriteSingleAsync(ParseAddress(channel), Convert.ToUInt16(value.ToString(), CultureInfo.InvariantCulture), token).ConfigureAwait(false); return; }
            if (_itech is not null) { if (channel == "SetVoltage") await _itech.WriteVoltageAsync(value.GetDouble(), token); else if (channel == "SetCurrent") await _itech.WriteCurrentAsync(value.GetDouble(), token); else if (channel == "OutputStatus") await _itech.WriteOutputAsync(value.ValueKind == JsonValueKind.True || value.ToString().Equals("1", StringComparison.OrdinalIgnoreCase), token); else throw new InvalidOperationException("ITECH 固定帧通道不可写"); return; }
            if (_bp is not null) { if (channel != "OutputEnabled") throw new InvalidOperationException("BP7330 通道不可写"); await _bp.WriteOutputAsync(value.GetBoolean(), token); return; }
            if (_scpi is not null) { var command = channel switch { "OutputStatus" => $"OUTPut {(value.ValueKind == JsonValueKind.True || value.ToString().Equals("1", StringComparison.OrdinalIgnoreCase) ? "ON" : "OFF")}", "SetTemperature" => $"TEMPerature {value.GetDouble().ToString(CultureInfo.InvariantCulture)}", _ => $"{channel} {value}" }; await _scpi.SendAsync(command, token); return; }
            throw new NotSupportedException($"{_model.Protocol} 尚未配置通道帧映射；不会使用 SCPI 或内存值替代");
        }
        private static ushort ParseAddress(string channel) { var index = channel.LastIndexOf('_'); return index > 0 && ushort.TryParse(channel[(index + 1)..], out var value) ? value : throw new InvalidOperationException("通道没有明确的寄存器地址"); }
        private static double ReadU32(byte[] frame, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(offset));
        private static JsonElement ParseText(string value) { if (bool.TryParse(value, out var boolean)) return JsonSerializer.SerializeToElement(boolean); if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonSerializer.SerializeToElement(number); return JsonSerializer.SerializeToElement(value); }
        public async ValueTask DisposeAsync() { if (_scpi is not null) await _scpi.DisposeAsync().ConfigureAwait(false); else if (_modbus is not null) await _modbus.DisposeAsync().ConfigureAwait(false); else if (_itech is not null) await _itech.DisposeAsync().ConfigureAwait(false); else if (_bp is not null) await _bp.DisposeAsync().ConfigureAwait(false); else if (_hzp is not null) await _hzp.DisposeAsync().ConfigureAwait(false); else if (_byte is not null) await _byte.DisposeAsync().ConfigureAwait(false); }
    }
}
