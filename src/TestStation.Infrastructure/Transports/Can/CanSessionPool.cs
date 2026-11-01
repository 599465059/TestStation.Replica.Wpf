using System.Text.Json;
using System.Runtime.CompilerServices;
using TestStation.Application.Hardware;
using TestStation.Application.Drivers;

namespace TestStation.Infrastructure.Transports.Can;

/// <summary>One native controller per physical endpoint; each consumer keeps its own subscription.</summary>
internal sealed class CanSessionPool
{
    internal sealed class Entry(ICanBusSession session, string signature, bool shared)
    {
        public readonly ICanBusSession Session = session;
        public readonly string Signature = signature;
        public readonly bool Shared = shared;
        public int Users = 1;
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    public ICanBusSession Create(Func<TransportEndpoint, string> key, Func<TransportEndpoint, ICanBusSession> factory)
        => new Handle(this, key, factory);

    private async Task<Entry> OpenAsync(string key, TransportEndpoint endpoint, Func<TransportEndpoint, ICanBusSession> factory, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var signature = string.Join(";", endpoint.Settings.Where(p => p.Key is "bitrate" or "data_bitrate" or "fd_enabled" or "listen_only")
                .OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value));
            if (_entries.TryGetValue(key, out var entry))
            {
                if (!entry.Shared || endpoint.LeaseMode != ResourceLeaseMode.SharedBus) throw new InvalidOperationException("CAN 端点已独占打开");
                if (entry.Signature != signature) throw new InvalidOperationException("共享 CAN 通道的速率、FD 和只听配置必须一致");
                if (entry.Session.State != TransportState.Open) throw new IOException("共享 CAN 会话已断开，请先断开所有使用者后重连");
                entry.Users++; return entry;
            }
            var session = factory(endpoint);
            try { await session.OpenAsync(endpoint, token).ConfigureAwait(false); }
            catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
            entry = new(session, signature, endpoint.LeaseMode == ResourceLeaseMode.SharedBus);
            _entries.Add(key, entry); return entry;
        }
        finally { _gate.Release(); }
    }
    private async Task ReleaseAsync(string key, Entry entry)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (--entry.Users != 0) return;
            _entries.Remove(key);
            await entry.Session.DisposeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private sealed class Handle(CanSessionPool pool, Func<TransportEndpoint, string> keyFactory, Func<TransportEndpoint, ICanBusSession> factory) : ICanBusSession
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private Entry? _entry;
        private CancellationTokenSource? _stop;
        private string _key = "";
        private bool _disposed;
        public string ResourceId { get; private set; } = "";
        public string Type => "can";
        public TransportState State => _entry?.Session.State ?? TransportState.Closed;
        public long DroppedFrameCount => _entry?.Session.DroppedFrameCount ?? 0;
        public event EventHandler? StateChanged;
        private void Changed(object? sender, EventArgs e) => StateChanged?.Invoke(this, e);
        public async Task OpenAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_entry is not null) throw new InvalidOperationException("CAN 会话已打开");
                _key = keyFactory(endpoint); ResourceId = endpoint.ResourceId;
                _entry = await pool.OpenAsync(_key, endpoint, factory, cancellationToken).ConfigureAwait(false);
                _stop = new(); _entry.Session.StateChanged += Changed;
                Changed(this, EventArgs.Empty);
            }
            finally { _gate.Release(); }
        }
        public async Task CloseAsync(CancellationToken cancellationToken = default)
        {
            _stop?.Cancel();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = _entry; _entry = null;
                if (entry is null) return;
                entry.Session.StateChanged -= Changed;
                try { await pool.ReleaseAsync(_key, entry).ConfigureAwait(false); }
                finally { _stop?.Dispose(); _stop = null; Changed(this, EventArgs.Empty); }
            }
            finally { _gate.Release(); }
        }
        public async Task SendAsync(CanFrame frame, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await (_entry?.Session ?? throw new IOException("CAN 会话未打开")).SendAsync(frame, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }
        public async Task<CanFrame> SendAndReceiveAsync(CanFrame frame, CanFrameFilter expectedFilter, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop?.Token ?? CancellationToken.None);
                return await (_entry?.Session ?? throw new IOException("CAN 会话未打开")).SendAndReceiveAsync(frame, expectedFilter, timeout, stop.Token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        public async IAsyncEnumerable<CanFrame> ReceiveAsync(CanFrameFilter? filter = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ICanBusSession session; CancellationTokenSource stop;
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                session = _entry?.Session ?? throw new IOException("CAN 会话未打开");
                stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop!.Token);
            }
            finally { _gate.Release(); }
            using (stop)
                await foreach (var frame in session.ReceiveAsync(filter, stop.Token).ConfigureAwait(false)) yield return frame;
        }
        public async ValueTask DisposeAsync() { if (_disposed) return; _disposed = true; await CloseAsync().ConfigureAwait(false); }
    }
}
