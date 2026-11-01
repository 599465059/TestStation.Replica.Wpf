using System.Threading.Channels;
using TestStation.Application.Hardware;

namespace TestStation.Infrastructure.Transports.Can;

/// <summary>
/// 会话级 CAN 帧分发器。底层接收循环只负责 Publish，订阅者拥有独立队列，
/// 因而一个消费者不会把帧从另一个消费者手里抢走。历史缓存用于容忍发送和订阅
/// 不在同一调用时序中的场景，例如先发送后订阅。
/// </summary>
internal sealed class CanFrameInbox
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Queue<CanFrame> _history = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private Exception? _completionError;
    private long _droppedFrameCount;

    public CanFrameInbox(int capacity = 256)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public long DroppedFrameCount { get { lock (_sync) return _droppedFrameCount; } }

    public Subscription Subscribe(CanFrameFilter? filter, bool replayExisting)
    {
        lock (_sync)
        {
            var subscription = new Subscription(this, filter);
            if (_completionError is not null)
            {
                subscription.Complete(_completionError);
                return subscription;
            }

            _subscriptions.Add(subscription);
            if (replayExisting)
                foreach (var frame in _history)
                    if (filter is null || filter.Matches(frame)) subscription.TryWrite(frame);
            return subscription;
        }
    }

    public void Publish(CanFrame frame)
    {
        frame.Validate();
        lock (_sync)
        {
            if (_completionError is not null) return;
            _history.Enqueue(frame);
            while (_history.Count > _capacity) { _history.Dequeue(); _droppedFrameCount++; }
            foreach (var subscription in _subscriptions.ToArray())
                if (subscription.Filter is null || subscription.Filter.Matches(frame)) subscription.TryWrite(frame);
        }
    }

    public void Complete(Exception? error = null)
    {
        lock (_sync)
        {
            if (_completionError is not null) return;
            _completionError = error ?? new IOException("CAN 接收会话已关闭");
            foreach (var subscription in _subscriptions.ToArray()) subscription.Complete(_completionError);
            _subscriptions.Clear();
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_sync)
        {
            _subscriptions.Remove(subscription);
            subscription.Complete();
        }
    }

    internal sealed class Subscription : IDisposable
    {
        private readonly CanFrameInbox _owner;
        private readonly Channel<CanFrame> _channel = Channel.CreateUnbounded<CanFrame>(new UnboundedChannelOptions
        {
            SingleWriter = false, SingleReader = false, AllowSynchronousContinuations = false
        });
        private int _disposed;

        internal Subscription(CanFrameInbox owner, CanFrameFilter? filter) { _owner = owner; Filter = filter; }
        internal CanFrameFilter? Filter { get; }
        internal bool TryWrite(CanFrame frame) => _channel.Writer.TryWrite(frame);
        internal void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
        public ValueTask<CanFrame> ReadNextAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAsync(cancellationToken);
        public IAsyncEnumerable<CanFrame> ReadAllAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAllAsync(cancellationToken);
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.Remove(this); }
    }
}
