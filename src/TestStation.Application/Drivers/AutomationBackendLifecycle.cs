namespace TestStation.Application.Drivers;

/// <summary>Transactional acquisition and exhaustive cleanup of composed device backends.</summary>
public static class AutomationBackendLifecycle
{
    public static async Task BeginAsync(IReadOnlyList<IAutomationChannelBackend> backends, IEnumerable<string> channels, CancellationToken token)
    {
        var requested = channels.ToArray();
        var attempted = new List<IAutomationChannelBackend>();
        try
        {
            foreach (var backend in backends)
            {
                attempted.Add(backend);
                await backend.BeginAutomationAsync(requested.Where(backend.OwnsChannel), token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            try { await EndAsync(attempted).ConfigureAwait(false); }
            catch (Exception cleanup) { error.Data["AutomationRollback"] = cleanup.ToString(); }
            throw;
        }
    }

    public static Task EndAsync(IEnumerable<IAutomationChannelBackend> backends) =>
        CompleteAllAsync(backends.Reverse().Select<IAutomationChannelBackend, Func<Task>>(backend => () => backend.EndAutomationAsync(CancellationToken.None)));

    public static Task DisposeAsync(IEnumerable<IAutomationChannelBackend> backends) =>
        CompleteAllAsync(backends.Reverse().Select<IAutomationChannelBackend, Func<Task>>(backend => () => backend.DisposeAsync().AsTask()));

    private static async Task CompleteAllAsync(IEnumerable<Func<Task>> operations)
    {
        var errors = new List<Exception>();
        foreach (var operation in operations)
            try { await operation().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("设备清理出现错误，已继续清理其余设备", errors);
    }
}
