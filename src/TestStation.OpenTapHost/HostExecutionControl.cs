using System.Diagnostics;

namespace TestStation.OpenTapHost;

// Pausing is cooperative: execution acknowledges it at the next Host checkpoint.
public sealed class HostExecutionControl
{
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _resume = new(true);
    private bool _requested;
    private bool _paused;
    private long _pauseStarted;
    private TimeSpan _pausedTime;

    public void Pause() { lock (_sync) { _requested = true; _resume.Reset(); } }
    public void Resume() { lock (_sync) { _requested = false; _resume.Set(); } }

    internal DateTimeOffset ExecutionTime
    {
        get
        {
            lock (_sync)
                return DateTimeOffset.UtcNow - _pausedTime - (_paused ? Stopwatch.GetElapsedTime(_pauseStarted) : TimeSpan.Zero);
        }
    }

    internal void Checkpoint(CancellationToken cancellation, Action<bool> notify)
    {
        lock (_sync)
        {
            if (!_requested) return;
            _paused = true;
            _pauseStarted = Stopwatch.GetTimestamp();
            notify(true);
        }
        try { _resume.Wait(cancellation); }
        finally
        {
            lock (_sync)
            {
                _pausedTime += Stopwatch.GetElapsedTime(_pauseStarted);
                _paused = false;
                if (!cancellation.IsCancellationRequested) notify(false);
            }
        }
    }
}
