using System.Collections.Concurrent;
using System.Text.Json;
using TestStation.Contracts;

namespace TestStation.OpenTapHost;

/// <summary>Requests I/O from the parent's shared device service over the existing JSON-lines protocol.</summary>
public sealed class HostChannelAccess(Func<HostEvent, Task> publish)
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private volatile bool _closed;

    /// <summary>The parent process is gone: fail pending and future device requests now instead of
    /// waiting 65 s each (CLEANUP keeps running after Stop and would otherwise stall).</summary>
    public void Close()
    {
        _closed = true;
        foreach (var pending in _pending.Values) pending.TrySetException(new IOException("与测试站主程序的连接已断开"));
    }

    public async Task<JsonElement> RequestAsync(string runId, string channelId, bool write, object? value, CancellationToken cancellationToken)
    {
        if (_closed) throw new IOException("与测试站主程序的连接已断开");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await publish(new HostEvent
            {
                Type = ExecutionProtocol.ChannelRequest, RunId = runId,
                Payload = JsonSerializer.SerializeToElement(new { request_id = id, channel_id = channelId, operation = write ? "write" : "read", value })
            }).ConfigureAwait(false);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(65), cancellationToken).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Complete(JsonElement response)
    {
        var id = response.GetProperty("request_id").GetString() ?? "";
        if (!_pending.TryGetValue(id, out var completion)) return; // Late responses after stop are harmless.
        if (response.TryGetProperty("error", out var error) && !string.IsNullOrEmpty(error.GetString()))
            completion.TrySetException(new IOException(error.GetString()));
        else completion.TrySetResult(response.GetProperty("value").Clone());
    }
}
