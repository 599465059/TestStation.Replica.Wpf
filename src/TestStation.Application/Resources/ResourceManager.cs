using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Application.Resources;

/// <summary>Serializes access to physical resources; RS485/CAN can opt into shared-bus leases.</summary>
public sealed class ResourceManager : IResourceManager, IDisposable
{
    private sealed class Slot
    {
        public readonly object Sync = new();
        public int Exclusive;
        public int Shared;
        public string Snapshot = "";
        public string Endpoint = "";
        public readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ResourceLeaseMode> Owners = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class Lease(ResourceManager owner, string resourceId, string ownerId, ResourceLeaseMode mode) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(resourceId, ownerId, mode); }
    }

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly IResourceCatalogService catalog;
    private bool _disposed;
    public event EventHandler? Changed;

    private readonly IReadOnlyDictionary<string, ICanVendorAdapter> _canAdapters;
    public ResourceManager(IResourceCatalogService catalog, IEnumerable<ICanVendorAdapter>? canAdapters = null)
    {
        this.catalog = catalog;
        _canAdapters = (canAdapters ?? []).ToDictionary(a => a.BackendId, StringComparer.OrdinalIgnoreCase);
        catalog.Changed += CatalogChanged;
        catalog.Changing += CatalogChanging;
        Reload();
    }

    public IReadOnlyList<TransportResource> GetResources() => catalog.Catalog.Transports.ToArray();
    public TransportResource? Find(string resourceId) => catalog.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(resourceId, StringComparison.OrdinalIgnoreCase));
    public bool IsLeased(string resourceId)
    {
        lock (_sync) return _slots.TryGetValue(resourceId, out var slot) && (slot.Exclusive > 0 || slot.Shared > 0);
    }

    public ResourceOccupancy GetOccupancy(string resourceId)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue(resourceId, out var slot))
                return new(resourceId, false, null, 0, new Dictionary<string, int>(), "");
            lock (slot.Sync)
            {
                var owners = new Dictionary<string, int>(slot.Counts, StringComparer.OrdinalIgnoreCase);
                var count = owners.Values.Sum();
                ResourceLeaseMode? mode = slot.Exclusive > 0 ? ResourceLeaseMode.Exclusive : slot.Shared > 0 ? ResourceLeaseMode.SharedBus : null;
                return new(resourceId, count > 0, mode, count, owners, slot.Endpoint);
            }
        }
    }

    public IDisposable Acquire(string resourceId, string owner, ResourceLeaseMode? mode = null)
    {
        var lease = AcquireCore(resourceId, owner, mode);
        Changed?.Invoke(this, EventArgs.Empty);
        return lease;
    }
    private IDisposable AcquireCore(string resourceId, string owner, ResourceLeaseMode? mode)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("资源占用者不能为空", nameof(owner));
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var resource = Find(resourceId) ?? throw new ResourceLeaseException($"通信资源不存在：{resourceId}");
            if (!resource.Enabled) throw new ResourceLeaseException($"通信资源已禁用：{resource.Name}");
            var effectiveMode = mode ?? (resource.LeaseMode.Equals("shared_bus", StringComparison.OrdinalIgnoreCase)
                ? ResourceLeaseMode.SharedBus : ResourceLeaseMode.Exclusive);
            if (!_slots.TryGetValue(resourceId, out var slot)) _slots[resourceId] = slot = new();
            lock (slot.Sync)
            {
                if (slot.Owners.TryGetValue(owner, out var existing))
                {
                    if (existing != effectiveMode) throw new ResourceLeaseException($"占用者已使用另一种资源模式：{owner}");
                    slot.Counts[owner]++;
                    return new Lease(this, resourceId, owner, effectiveMode);
                }
                if (effectiveMode == ResourceLeaseMode.Exclusive && slot.Owners.Count > 0 || effectiveMode == ResourceLeaseMode.SharedBus && slot.Exclusive > 0)
                    throw new ResourceLeaseException($"通信资源正在被占用：{resource.Name}");
                var endpoint = EndpointKey(resource);
                if (_slots.Any(p => p.Key != resourceId && p.Value.Owners.Count > 0 && p.Value.Endpoint == endpoint &&
                    !(endpoint.StartsWith("can:", StringComparison.OrdinalIgnoreCase) && effectiveMode == ResourceLeaseMode.SharedBus &&
                      p.Value.Owners.Values.All(existingMode => existingMode == ResourceLeaseMode.SharedBus))))
                    throw new ResourceLeaseException("同一物理通信端点已被其他设备占用");
                slot.Snapshot = JsonSerializer.Serialize(resource); slot.Endpoint = endpoint;
                slot.Counts[owner] = 1;
                slot.Owners[owner] = effectiveMode;
                if (effectiveMode == ResourceLeaseMode.Exclusive) slot.Exclusive++; else slot.Shared++;
                return new Lease(this, resourceId, owner, effectiveMode);
            }
        }
    }

    private void Release(string resourceId, string owner, ResourceLeaseMode mode)
    {
        lock (_sync)
        {
            if (!_slots.TryGetValue(resourceId, out var slot)) return;
            lock (slot.Sync)
            {
                if (!slot.Counts.TryGetValue(owner, out var count)) return;
                if (count > 1) { slot.Counts[owner] = count - 1; return; }
                slot.Counts.Remove(owner);
                if (!slot.Owners.Remove(owner)) return;
                if (mode == ResourceLeaseMode.Exclusive) slot.Exclusive = Math.Max(0, slot.Exclusive - 1); else slot.Shared = Math.Max(0, slot.Shared - 1);
                if (slot.Owners.Count == 0) _slots.Remove(resourceId);
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string EndpointKey(TransportResource resource)
    {
        var values = TransportSettingsAccessor.Read(resource);
        var backend = (values.CanBackendId ?? "").ToLowerInvariant();
        if (backend == "peak") backend = "pcan";
        var channel = values.CanChannel ?? "";
        if (_canAdapters.TryGetValue(backend, out var adapter)) channel = adapter.NormalizeChannel(channel);
        return TransportSettingsAccessor.NormalizeType(resource.Type) switch
        {
            "tcp" => "tcp:" + ((values.Host ?? "").ToLowerInvariant() is "localhost" or "::1" ? "127.0.0.1" : (values.Host ?? "").ToLowerInvariant()) + ":" + values.TcpPort,
            "serial" => "serial:" + values.Port?.ToUpperInvariant(),
            "can" => "can:" + backend + ":" + channel.ToLowerInvariant(),
            "visa" => "visa:" + (values.VisaResource ?? "").ToLowerInvariant(),
            _ => resource.Type + ":" + JsonSerializer.Serialize(resource.Settings)
        };
    }
    private void CatalogChanging(object? sender, ResourceCatalogChangingEventArgs args)
    {
        lock (_sync)
            foreach (var pair in _slots.Where(p => p.Value.Owners.Count > 0))
            {
                var next = args.Catalog.Transports.FirstOrDefault(r => r.Id.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
                if (args.Root != catalog.ProjectRoot || next is null || JsonSerializer.Serialize(next) != pair.Value.Snapshot)
                    throw new ResourceLeaseException("通信资源已连接，请先断开后修改或切换工程");
            }
    }
    private void CatalogChanged(object? sender, EventArgs e) { Reload(); Changed?.Invoke(this, EventArgs.Empty); }
    private void Reload()
    {
        lock (_sync)
        {
            foreach (var resource in catalog.Catalog.Transports)
                if (!_slots.ContainsKey(resource.Id)) _slots[resource.Id] = new();
            foreach (var id in _slots.Keys.Where(id => catalog.Catalog.Transports.All(r => !r.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToArray()) _slots.Remove(id);
        }
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _slots.Clear(); }
        catalog.Changed -= CatalogChanged;
        catalog.Changing -= CatalogChanging;
    }
}
