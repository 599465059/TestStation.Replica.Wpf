using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTap;
using TestStation.Contracts;

namespace TestStation.OpenTapHost;

public sealed record HostRunRequest
{
    [JsonPropertyName("protocol_version")] public int ProtocolVersion { get; init; } = ExecutionProtocol.Version;
    [JsonPropertyName("run_id")] public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("sequence")] public required JsonElement Sequence { get; init; }
    [JsonPropertyName("sequences")] public IReadOnlyList<JsonElement> Sequences { get; init; } = [];
    [JsonPropertyName("parameters")] public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = new Dictionary<string, JsonElement>();
    [JsonPropertyName("simulation_values")] public IReadOnlyDictionary<string, JsonElement> SimulationValues { get; init; } = new Dictionary<string, JsonElement>();
    [JsonPropertyName("external_channel_types")] public IReadOnlyDictionary<string, string>? ExternalChannelTypes { get; init; }
    [JsonPropertyName("external_channels")] public IReadOnlyList<string> ExternalChannels { get; init; } = [];
    [JsonPropertyName("global_definitions")] public IReadOnlyList<JsonElement> GlobalDefinitions { get; init; } = [];
    [JsonPropertyName("station_definitions")] public IReadOnlyList<JsonElement> StationDefinitions { get; init; } = [];
    [JsonPropertyName("global_values")] public IReadOnlyDictionary<string, JsonElement> GlobalValues { get; init; } = new Dictionary<string, JsonElement>();
    [JsonPropertyName("station_values")] public IReadOnlyDictionary<string, JsonElement> StationValues { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record HostEvent
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("run_id")] public string RunId { get; init; } = "";
    [JsonPropertyName("step_id")] public string StepId { get; init; } = "";
    [JsonPropertyName("step_run_id")] public string StepRunId { get; init; } = "";
    [JsonPropertyName("parent_step_run_id")] public string ParentStepRunId { get; init; } = "";
    [JsonPropertyName("sequence_id")] public string SequenceId { get; init; } = "";
    [JsonPropertyName("section")] public string Section { get; init; } = "";
    [JsonPropertyName("path")] public string Path { get; init; } = "";
    [JsonPropertyName("status")] public string Status { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("payload")] public JsonElement? Payload { get; init; }
}

public interface IOpenTapRunner
{
    Task RunAsync(HostRunRequest request, Func<HostEvent, Task> publish, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task ResumeAsync();
}

public sealed class OpenTapRunner : IOpenTapRunner
{
    private readonly HostChannelAccess? _channelAccess;
    public OpenTapRunner(HostChannelAccess? channelAccess = null) => _channelAccess = channelAccess;
    private readonly SequenceCompiler _compiler = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _runCancellation;
    private HostExecutionControl? _control;

    public async Task RunAsync(HostRunRequest request, Func<HostEvent, Task> publish, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var control = new HostExecutionControl();
        lock (_sync)
        {
            if (_runCancellation is not null) throw new InvalidOperationException("Host 正在执行序列");
            _runCancellation = linked;
            _control = control;
        }
        try
        {
            // Register controls before leaving the stdin loop, then move engine work off it.
            await Task.Run(async () =>
            {
                var plan = _compiler.Compile(request.Sequence, request.Sequences);
                linked.Token.ThrowIfCancellationRequested();
                var context = new HostExecutionContext(publish, linked.Token, control)
                { RunId = request.RunId, ExternalChannelTypes = request.ExternalChannelTypes ?? new Dictionary<string, string>(), ChannelAccess = _channelAccess, ExternalChannels = request.ExternalChannels.ToHashSet(StringComparer.OrdinalIgnoreCase) };
                context.InitializeShared(request.GlobalDefinitions, request.StationDefinitions, request.GlobalValues, request.StationValues);
                foreach (var pair in request.SimulationValues.Where(p => !context.ExternalChannels.Contains(p.Key)))
                    context.SimulationValues[pair.Key] = HostExecutionContext.JsonValue(pair.Value);
                var arguments = request.Parameters.ToDictionary(pair => pair.Key, pair => HostExecutionContext.JsonValue(pair.Value), StringComparer.OrdinalIgnoreCase);
                await publish(new HostEvent
                {
                    Type = "plan_compiled", RunId = context.RunId, SequenceId = plan.SequenceId, Status = "ready",
                    Message = $"已编译序列: {plan.SequenceName}", Payload = JsonSerializer.SerializeToElement(plan)
                });
                var testPlan = OpenTapPlanBuilder.Build(plan, context, arguments);
                var listener = new HostResultListener(context);
                // A null selection executes the whole plan. An empty set selects no steps.
                // Stop is cooperative through HostExecutionContext, not OpenTAP's abort token:
                // OpenTAP registers that token to TapThread.Abort, after which every child step
                // throws on entry and the CLEANUP section could never run.
                var run = await testPlan.ExecuteAsync(new[] { listener }, Array.Empty<ResultParameter>(), null, CancellationToken.None);
                run.WaitForResults();
            });
        }
        finally
        {
            lock (_sync) { _runCancellation = null; _control = null; }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync) _runCancellation?.Cancel();
        return Task.CompletedTask;
    }

    public Task PauseAsync() { lock (_sync) _control?.Pause(); return Task.CompletedTask; }
    public Task ResumeAsync() { lock (_sync) _control?.Resume(); return Task.CompletedTask; }
}
