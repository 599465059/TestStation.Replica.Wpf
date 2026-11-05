using System.Collections.Concurrent;
using System.Text.Json;
using TestStation.Application;
using TestStation.Application.Devices.ItechPowerSupply;
using TestStation.Application.Drivers;
using TestStation.Application.Hardware;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;
using TestStation.Infrastructure.Protocols.Scpi;

namespace TestStation.Infrastructure.Devices.ItechPowerSupply;

/// <summary>One session per project device. Manual panels and the isolated engine share these sessions.</summary>
public sealed class PowerSupplyDeviceService : IPowerSupplyDeviceService
{
    public const string FamilyId = "itech.power-supply";
    private sealed record Active(IDeviceSession Session, IDisposable Lease, string Definition);
    private readonly IResourceCatalogService _catalog;
    private readonly IResourceManager _resources;
    private readonly IItechPowerSupplyProfileStore _profiles;
    private readonly ItechPowerSupplyDriverFactory _driverFactory;
    private readonly ConcurrentDictionary<string, Active> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PowerSupplyConnection> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _initializedRoot;
    private bool _initializing;
    private bool _disposed;
    private bool _familyEnabled = true;
    private volatile bool _automationActive;
    private readonly HashSet<string> _automationOwned = new(StringComparer.OrdinalIgnoreCase);
    private string _catalogSnapshot = "";
    public bool IsAutomationActive { get => _automationActive; set => _automationActive = value; }
    public bool FamilyEnabled
    {
        get => _familyEnabled;
        set
        {
            if (_familyEnabled == value) return;
            EnsureManual();
            _familyEnabled = value;
            if (!value && _sessions.Count > 0) _ = DisconnectAfterDisableAsync();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool HasProject => _catalog.ProjectRoot is not null;
    public IReadOnlyList<DeviceDefinition> Devices => _catalog.Catalog.Devices.Where(d => d.FamilyId == FamilyId).Select(Clone).ToArray();
    public IReadOnlyList<TransportResource> Resources => _catalog.Catalog.Transports.Select(Clone).ToArray();
    public event EventHandler? Changed;
    public event EventHandler? StateChanged;
    public event EventHandler<DeviceTraffic>? Traffic;
    public event EventHandler<ChannelRuntimeValue>? ValueChanged;
    public async Task BeginAutomationAsync(IEnumerable<string> channels, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        var opened = new List<string>();
        try
        {
            EnsureManual(); IsAutomationActive = true;
            foreach (var id in channels.Where(OwnsChannel).Select(c => c.Split('.', 2)[0]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (_sessions.ContainsKey(id)) continue;
                await ConnectCoreAsync(id, token).ConfigureAwait(false); opened.Add(id);
            }
            lock (_automationOwned) foreach (var id in opened) _automationOwned.Add(id);
        }
        catch
        {
            foreach (var id in opened) await CloseCoreAsync(id, safeOff: true).ConfigureAwait(false);
            IsAutomationActive = false; throw;
        }
        finally { _gate.Release(); }
    }
    public bool OwnsChannel(string channelId) => Devices.Any(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase));

    public async Task EndAutomationAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            HashSet<string> owned;
            lock (_automationOwned)
            {
                owned = new HashSet<string>(_automationOwned, StringComparer.OrdinalIgnoreCase);
                _automationOwned.Clear();
            }
            // Sessions opened for this run are closed with the output switched off. Failures are shown
            // as device errors; this runs from the automation lease release and must not throw.
            foreach (var id in owned) await CloseCoreAsync(id, safeOff: true).ConfigureAwait(false);
            IsAutomationActive = false;
        }
        finally { _gate.Release(); }
    }
    public PowerSupplyDeviceService(IResourceCatalogService catalog, IResourceManager resources,
        ITransportSessionFactory transports, IItechPowerSupplyProfileStore profiles, ItechPowerSupplyDriverFactory? driverFactory = null)
    {
        _catalog = catalog; _resources = resources; _profiles = profiles;
        _driverFactory = driverFactory ?? new ItechPowerSupplyDriverFactory(transports, profiles);
        _catalog.Changed += CatalogChanged; _catalog.Changing += CatalogChanging; _profiles.Changed += ProfilesChanged;
        if (HasProject) CatalogChanged(this, EventArgs.Empty);
    }
    private static T Clone<T>(T item) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(item))!;
    private DeviceDefinition Find(string id) => Devices.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"电源实例不存在：{id}");
    public PowerSupplyModel ModelFor(string deviceId)
    { var device = Find(deviceId); return _profiles.Family.Models.FirstOrDefault(m => m.Id == device.ModelId) ?? throw new InvalidOperationException($"型号已删除：{device.ModelId}"); }
    public IReadOnlyList<PowerSupplyChannel> ChannelsFor(string deviceId)
    {
        var family = _profiles.Family; var model = ModelFor(deviceId);
        return family.Resolve(model).Where(c => c.Enabled && (c.Binding switch
        { "voltage" or "voltage_measurement" => model.Voltage, "current" or "current_measurement" => model.Current, "output" => model.Output, "protection" => model.Protection, _ => true }))
            .Select(c => c with { Id = deviceId + "." + c.Id }).ToArray();
    }
    public IReadOnlyList<string> ChannelIds => Devices.SelectMany(d => _profiles.Family.Resolve(ModelFor(d.Id)).Select(c => d.Id + "." + c.Id)).ToArray();
    public IReadOnlyList<SequenceChannelDefinition> GetChannels() => !FamilyEnabled ? [] : Devices.Where(d => ModelFor(d.Id).Enabled).SelectMany(d => ChannelsFor(d.Id)
        .Select(c => new SequenceChannelDefinition(c.Id, c.DataType, d.Name + " / " + c.DisplayName, c.CanRead, c.CanWrite))).ToArray();
    public PowerSupplyConnection ConnectionFor(string deviceId) => _states.GetValueOrDefault(deviceId) ?? new();
    public bool TurnOutputOffBeforeDisconnect { get; set; } = true;
    private static bool IsOutputChannel(string channelId) => channelId.EndsWith(".Output.Enabled", StringComparison.OrdinalIgnoreCase);
    private PowerSupplyChannel? OutputChannel(string deviceId)
    {
        try { return ChannelsFor(deviceId).FirstOrDefault(c => c.Binding == "output" && c.CanWrite); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { return null; } // device or model removed
    }
    /// <summary>Records the output state from a successful read or write; raises StateChanged only on change.</summary>
    private void TrackOutput(string deviceId, string channelId, JsonElement value)
    {
        if (!IsOutputChannel(channelId) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return;
        var enabled = value.GetBoolean();
        var current = ConnectionFor(deviceId);
        if (current.Connected && current.OutputEnabled != enabled) SetState(deviceId, current with { OutputEnabled = enabled });
    }
    private void SetState(string id, PowerSupplyConnection state) { _states[id] = state; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void EnsureManual()
    { ObjectDisposedException.ThrowIf(_disposed, this); if (IsAutomationActive) throw new InvalidOperationException("自动测试期间不能修改设备或家族配置"); }
    private void SaveCatalog(ResourceCatalog catalog) { EnsureManual(); _catalog.Save(catalog); }
    public DeviceDefinition AddDevice(string name)
    {
        EnsureManual(); if (!HasProject) throw new InvalidOperationException("请先打开工程");
        var catalog = Clone(_catalog.Catalog); var i = 1;
        while (catalog.Devices.Any(d => d.Id.Equals($"ITECH{i:00}", StringComparison.OrdinalIgnoreCase))) i++;
        var device = new DeviceDefinition { Id = $"ITECH{i:00}", Name = RequiredName(name), FamilyId = FamilyId,
            ModelId = _profiles.Family.Models.First(m => m.Enabled).Id, DriverId = "itech.public-scpi", ProtocolId = "scpi", TransportId = "" };
        catalog.Devices.Add(device); SaveCatalog(catalog); return Clone(device);
    }
    private static string RequiredName(string name) => string.IsNullOrWhiteSpace(name) ? throw new InvalidDataException("名称不能为空") : name.Trim();
    public void UpdateDevice(string id, string name, string modelId, string resourceId)
    {
        EnsureManual(); if (ConnectionFor(id).Connected) throw new InvalidOperationException("请先断开设备后修改");
        var model = _profiles.Family.Models.FirstOrDefault(m => m.Id == modelId && m.Enabled) ?? throw new InvalidOperationException("型号不可用");
        var catalog = Clone(_catalog.Catalog);
        var resource = string.IsNullOrWhiteSpace(resourceId) ? null : catalog.Transports.FirstOrDefault(r => r.Id == resourceId && r.Enabled);
        if (!string.IsNullOrWhiteSpace(resourceId) && resource is null) throw new InvalidOperationException("通信资源不可用");
        var resourceType = resource is null ? "" : TransportSettingsAccessor.NormalizeType(resource.Type);
        if (resource is not null && (resourceType is not ("tcp" or "serial") || resourceType == "tcp" && !model.Tcp || resourceType == "serial" && !model.Serial)) throw new InvalidOperationException("该型号不支持所选通信资源");
        if (resource is not null && Devices.Any(d => d.Id != id && d.TransportId.Equals(resource.Id, StringComparison.OrdinalIgnoreCase) && ConnectionFor(d.Id).Connected))
            throw new InvalidOperationException("通信资源已被其他已连接设备占用");
        var device = catalog.Devices.First(d => d.Id == id); device.Name = RequiredName(name); device.ModelId = modelId; device.TransportId = resourceId;
        SaveCatalog(catalog);
    }
    public void RemoveDevice(string id)
    { EnsureManual(); if (ConnectionFor(id).Connected) throw new InvalidOperationException("请先断开设备"); var catalog = Clone(_catalog.Catalog); catalog.Devices.RemoveAll(d => d.Id == id); SaveCatalog(catalog); }
    public void SaveResource(TransportResource resource)
    {
        EnsureManual(); TransportSettingsAccessor.ValidateEndpoint(resource);
        if (_sessions.Values.Any(active => string.Equals(JsonSerializer.Deserialize<DeviceDefinition>(active.Definition)?.TransportId, resource.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("通信资源已被连接设备占用，请先断开设备");
        var catalog = Clone(_catalog.Catalog); var index = catalog.Transports.FindIndex(r => r.Id == resource.Id);
        if (index >= 0) catalog.Transports[index] = Clone(resource); else catalog.Transports.Add(Clone(resource));
        SaveCatalog(catalog);
    }
    public async Task SaveFamilyAsync(PowerSupplyFamily family, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureManual(); PowerSupplyValidation.Validate(family);
            if (_sessions.Count > 0) throw new InvalidOperationException("请先确认并断开所有电源，再保存家族配置");
            if (Devices.Any(d => !family.Models.Any(m => m.Id == d.ModelId && m.Enabled))) throw new InvalidOperationException("工程中的设备仍引用被删除或禁用的型号，请先调整设备型号");
            _profiles.SaveFamily(family);
        }
        finally { _gate.Release(); }
    }
    public async Task ConnectAsync(string deviceId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { EnsureManual(); await ConnectCoreAsync(deviceId, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task ConnectCoreAsync(string deviceId, CancellationToken token)
    {
        if (_sessions.ContainsKey(deviceId)) return;
        if (!FamilyEnabled) throw new InvalidOperationException("电源家族已禁用");
        var device = Find(deviceId);
        var model = ModelFor(deviceId);
        if (!model.Enabled) throw new InvalidOperationException("型号已禁用");
        var resource = Resources.FirstOrDefault(r => r.Id == device.TransportId && r.Enabled)
            ?? throw new InvalidOperationException("通信资源不可用");
        var transportType = TransportSettingsAccessor.NormalizeType(resource.Type);
        if (transportType is not ("tcp" or "serial") ||
            transportType == "tcp" && !model.Tcp || transportType == "serial" && !model.Serial)
            throw new NotSupportedException("该型号不支持此传输；VISA/CAN 尚未接入");
        TransportSettingsAccessor.ValidateEndpoint(resource);
        foreach (var active in _sessions.Values)
        {
            var activeDevice = JsonSerializer.Deserialize<DeviceDefinition>(active.Definition);
            var activeResource = Resources.FirstOrDefault(r => r.Id.Equals(activeDevice?.TransportId, StringComparison.OrdinalIgnoreCase));
            if (activeResource is not null && SamePhysicalEndpoint(activeResource, resource))
                throw new ResourceLeaseException("同一个物理通信端点已被其他设备占用");
        }
        var lease = _resources.Acquire(resource.Id, "device:" + device.Id, ResourceLeaseMode.Exclusive);
        IDeviceSession? session = null;
        try
        {
            var createdSession = _driverFactory.CreateSession(device, resource, record => Traffic?.Invoke(this, new(record.Time, device.Id, record.Direction, record.Text)));
            session = createdSession;
            await createdSession.ConnectAsync(token).ConfigureAwait(false);
            var identity = await createdSession.QueryAsync("*IDN?", token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(identity)) throw new InvalidDataException("设备身份响应为空");
            bool? output = null;
            var outputChannel = ChannelsFor(device.Id).FirstOrDefault(c => c.Binding == "output" && c.CanRead);
            if (outputChannel is not null)
            {
                using var outputTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                outputTimeout.CancelAfter(Math.Max(outputChannel.TimeoutMs, 1500));
                var state = await createdSession.ReadAsync(outputChannel.Id, outputTimeout.Token).ConfigureAwait(false);
                if (state.ValueKind is JsonValueKind.True or JsonValueKind.False) output = state.GetBoolean();
            }
            _sessions[device.Id] = new(session, lease, JsonSerializer.Serialize(device));
            SetState(device.Id, new(true, identity, OutputEnabled: output));
        }
        catch (Exception error)
        {
            try { if (session is not null) await session.DisposeAsync().ConfigureAwait(false); }
            finally { lease.Dispose(); }
            SetState(device.Id, new(Error: error.Message));
            throw;
        }
    }
    /// <summary>
    /// Closes one session. With <paramref name="safeOff"/> (and the policy enabled) the output is
    /// switched off first; a failure is returned so callers can report it, and also shown as the
    /// device error. I/O-failure paths pass safeOff = false: the link is already broken.
    /// </summary>
    private async Task<string?> CloseCoreAsync(string id, string error = "", bool safeOff = false)
    {
        string? failure = null;
        if (_sessions.TryRemove(id, out var active))
        {
            try
            {
                if (safeOff && TurnOutputOffBeforeDisconnect && error.Length == 0 && OutputChannel(id) is { } output)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(Math.Max(output.TimeoutMs, 1500));
                        await active.Session.WriteAsync(output.Id, JsonSerializer.SerializeToElement(false), timeout.Token).ConfigureAwait(false);
                    }
                    catch (Exception off) when (off is not OutOfMemoryException)
                    {
                        failure = $"关闭输出失败：{off.Message}。请到现场确认电源输出状态";
                    }
                }
                await active.Session.DisposeAsync().ConfigureAwait(false);
            }
            finally { active.Lease.Dispose(); }
        }
        SetState(id, new(Error: failure ?? error));
        return failure;
    }

    private static void ThrowIfFailed(IReadOnlyCollection<string> failures)
    {
        if (failures.Count > 0) throw new IOException(string.Join("；", failures));
    }
    private static bool SamePhysicalEndpoint(TransportResource left, TransportResource right)
    {
        if (!left.Type.Equals(right.Type, StringComparison.OrdinalIgnoreCase)) return false;
        var a = TransportSettingsAccessor.Read(left);
        var b = TransportSettingsAccessor.Read(right);
        var type = TransportSettingsAccessor.NormalizeType(left.Type);
        return type.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.TcpPort == b.TcpPort
            : type.Equals("serial", StringComparison.OrdinalIgnoreCase) && string.Equals(a.Port, b.Port, StringComparison.OrdinalIgnoreCase);
    }
    private async Task DisconnectAfterDisableAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in _sessions.Keys.ToArray()) await CloseCoreAsync(id, safeOff: true).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task DisconnectDeviceAsync(string deviceId, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureManual();
            var failure = await CloseCoreAsync(deviceId, safeOff: true).ConfigureAwait(false);
            if (failure is not null) throw new IOException(failure);
        }
        finally { _gate.Release(); }
    }
    /// <summary>Disconnects every session (project switch, app exit); all are closed even if one fails.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureManual();
            var failures = new List<string>();
            foreach (var id in _sessions.Keys.ToArray())
                if (await CloseCoreAsync(id, safeOff: true).ConfigureAwait(false) is { } failure)
                    failures.Add($"{Devices.FirstOrDefault(d => d.Id == id)?.Name ?? id}：{failure}");
            ThrowIfFailed(failures);
        }
        finally { _gate.Release(); }
    }
    private (string DeviceId, PowerSupplyChannel Channel, IDeviceSession Session) Resolve(string channelId)
    {
        if (!FamilyEnabled) throw new InvalidOperationException("电源家族已禁用");
        var device = Devices.FirstOrDefault(d => channelId.StartsWith(d.Id + ".", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"未知设备通道：{channelId}");
        if (!ModelFor(device.Id).Enabled) throw new InvalidOperationException("型号已禁用");
        var channel = ChannelsFor(device.Id).FirstOrDefault(c => c.Id.Equals(channelId, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException($"通道已禁用或不存在：{channelId}");
        if (!_sessions.TryGetValue(device.Id, out var active)) throw new IOException($"设备 {device.Name} 未连接，请手动连接");
        return (device.Id, channel, active.Session);
    }
    public async Task<JsonElement> ReadAsync(string channelId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (id, channel, session) = Resolve(channelId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(channel.TimeoutMs);
            try
            {
                var value = await session.ReadAsync(channelId, timeout.Token).ConfigureAwait(false);
                TrackOutput(id, channelId, value);
                ValueChanged?.Invoke(this, new(channelId, value, DateTimeOffset.UtcNow));
                return value;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidDataException or FormatException) { await CloseCoreAsync(id, e.Message).ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    public async Task WriteAsync(string channelId, JsonElement value, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (id, channel, session) = Resolve(channelId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(channel.TimeoutMs);
            try { await session.WriteAsync(channelId, value, timeout.Token).ConfigureAwait(false); TrackOutput(id, channelId, value); ValueChanged?.Invoke(this, new(channelId, value, DateTimeOffset.UtcNow)); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidDataException or FormatException) { await CloseCoreAsync(id, e.Message).ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }
    private void CatalogChanging(object? sender, ResourceCatalogChangingEventArgs args)
    {
        if (IsAutomationActive && (args.Root != _catalog.ProjectRoot || JsonSerializer.Serialize(args.Catalog) != _catalogSnapshot)) throw new InvalidOperationException("自动测试期间不能修改设备资源");
        foreach (var pair in _sessions)
        {
            var next = args.Catalog.Devices.FirstOrDefault(d => d.Id == pair.Key);
            if (args.Root != _catalog.ProjectRoot || next is null || JsonSerializer.Serialize(next) != pair.Value.Definition) throw new InvalidOperationException("已连接设备不能修改或删除，请先断开");
            var current = _catalog.Catalog.Devices.FirstOrDefault(d => d.Id == pair.Key);
            var resourceId = current?.TransportId;
            if (!string.IsNullOrWhiteSpace(resourceId))
            {
                var oldResource = _catalog.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
                var newResource = args.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
                if (oldResource is null || newResource is null || JsonSerializer.Serialize(oldResource) != JsonSerializer.Serialize(newResource))
                    throw new InvalidOperationException("已连接设备的通信资源不能修改或删除，请先断开");
            }
        }
    }
    private void CatalogChanged(object? sender, EventArgs args)
    {
        if (_initializing) return;
        if (HasProject && _initializedRoot != _catalog.ProjectRoot)
        {
            _initializing = true;
            try
            {
                TryMigrateLegacyConfiguration();
                _initializedRoot = _catalog.ProjectRoot;
            }
            finally { _initializing = false; }
        }
        _catalogSnapshot = JsonSerializer.Serialize(_catalog.Catalog);
        Changed?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty);
    }
    private void TryMigrateLegacyConfiguration()
    {
        if (Devices.Count > 0) return;
        var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TestStation", "scpi-power-supply.json");
        if (!File.Exists(settingsPath)) return;
        ScpiEndpoint endpoint;
        try { endpoint = JsonSerializer.Deserialize<ScpiEndpoint>(File.ReadAllText(settingsPath)) ?? new(); }
        catch { return; }

        var catalog = Clone(_catalog.Catalog);
        if (catalog.Devices.Any(d => d.Id.Equals("ITECH01", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ITECH01 已被其他家族占用，无法迁移");
        var resource = new TransportResource
        {
            Id = "itech_tcp_" + Guid.NewGuid().ToString("N")[..8],
            Name = "迁移的 ITECH TCP",
            Type = "tcp"
        };
        TransportSettingsAccessor.Write(resource, new(Host: endpoint.Host, TcpPort: endpoint.Port, ConnectTimeoutMs: endpoint.TimeoutMs));
        catalog.Transports.Add(resource);
        var model = _profiles.Family.Models.FirstOrDefault(m => m.Enabled)
            ?? throw new InvalidDataException("没有可用于迁移的启用型号");
        catalog.Devices.Add(new DeviceDefinition
        {
            Id = "ITECH01", Name = "电源 01", FamilyId = FamilyId, ModelId = model.Id,
            DriverId = model.DriverId, ProtocolId = model.ProtocolId, TransportId = resource.Id
        });
        _catalog.Save(catalog);
    }
    private void ProfilesChanged(object? sender, EventArgs args) { Changed?.Invoke(this, EventArgs.Empty); StateChanged?.Invoke(this, EventArgs.Empty); }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; _catalog.Changed -= CatalogChanged; _catalog.Changing -= CatalogChanging; _profiles.Changed -= ProfilesChanged; foreach (var id in _sessions.Keys.ToArray()) await CloseCoreAsync(id, safeOff: true).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
