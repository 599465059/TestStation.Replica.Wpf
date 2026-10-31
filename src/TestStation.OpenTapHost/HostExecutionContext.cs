using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestStation.Application;

namespace TestStation.OpenTapHost;

internal enum HostFlow { None, Break, Continue, Stop, Cleanup }

internal sealed class HostSequenceFrame
{
    public string SequenceId { get; init; } = "";
    public string SequenceName { get; init; } = "";
    public Stack<string> Loops { get; } = new();
    public Dictionary<string, object?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> VariableTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HostFlow Flow { get; set; }
    public int LoopDepth { get; set; }
}

internal sealed record HostStepMetadata(string StepId, string SequenceId, string Section, string Path, bool RecordResult);
internal sealed record HostStepOutcome(string Status, string Message);

public sealed class HostExecutionContext
{
    private readonly Stack<HostSequenceFrame> _frames = new();
    private readonly List<DateTimeOffset> _deadlines = [];
    private readonly Func<HostEvent, Task> _publish;
    private readonly CancellationToken _cancellation;
    private readonly HostExecutionControl _control;
    private bool _stopRequested;
    private int _cleanupDepth;
    internal Dictionary<Guid, HostStepMetadata> Steps { get; } = [];
    internal System.Collections.Concurrent.ConcurrentDictionary<Guid, HostStepOutcome> Outcomes { get; } = new();
    internal System.Collections.Concurrent.ConcurrentQueue<HostEvent> RecordedSteps { get; } = new();
    internal IReadOnlyDictionary<string, object?> FinalValues { get; private set; } = new Dictionary<string, object?>();
    internal Dictionary<string, object?> Channels { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, object?> SimulationValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HashSet<string> ExternalChannels { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    internal IReadOnlyDictionary<string, string> ExternalChannelTypes { get; init; } = new Dictionary<string, string>();
    internal HostChannelAccess? ChannelAccess { get; init; }

    internal object? ReadSignal(string channel)
    {
        if (ExternalChannels.Contains(channel))
        {
            var access = ChannelAccess ?? throw new IOException("设备访问服务未连接");
            return JsonValue(access.RequestAsync(RunId, channel, false, null,
                _cleanupDepth > 0 ? CancellationToken.None : _cancellation).GetAwaiter().GetResult());
        }
        return SimulationValues.TryGetValue(channel, out var value) ? value : throw new InvalidDataException($"模拟通道未配置: {channel}");
    }

    internal void WriteSignal(string channel, object? value)
    {
        if (ExternalChannels.Contains(channel))
        {
            var access = ChannelAccess ?? throw new IOException("设备访问服务未连接");
            access.RequestAsync(RunId, channel, true, value,
                _cleanupDepth > 0 ? CancellationToken.None : _cancellation).GetAwaiter().GetResult();
        }
        else SimulationValues[channel] = value;
    }
    internal Dictionary<string, object?> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, object?> Station { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> GlobalTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, string> StationTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal HostSequenceFrame Frame => _frames.Peek();
    internal string RunId { get; set; } = "";
    internal bool WasStopped => _cancellation.IsCancellationRequested;
    internal bool ShouldStopBody => _stopRequested && _cleanupDepth == 0;

    internal void InitializeShared(IReadOnlyList<JsonElement> globalDefinitions, IReadOnlyList<JsonElement> stationDefinitions,
        IReadOnlyDictionary<string, JsonElement> globalValues, IReadOnlyDictionary<string, JsonElement> stationValues)
    {
        foreach (var definition in globalDefinitions) AddShared(definition, Globals, GlobalTypes, globalValues);
        foreach (var definition in stationDefinitions) AddShared(definition, Station, StationTypes, stationValues);
        PublishSharedValues();
    }

    private static void AddShared(JsonElement definition, Dictionary<string, object?> values, Dictionary<string, string> types,
        IReadOnlyDictionary<string, JsonElement> supplied)
    {
        var name = definition.GetProperty("name").GetString() ?? "";
        var type = definition.TryGetProperty("data_type", out var dt) ? dt.GetString() ?? "number" : "number";
        var initial = supplied.TryGetValue(name, out var current) ? JsonValue(current) :
            definition.TryGetProperty("default", out var initialElement) && initialElement.ValueKind != JsonValueKind.Null ? JsonValue(initialElement) : null;
        if (initial is null) initial = type.EndsWith("[]", StringComparison.Ordinal) ? Array.Empty<object?>() : type == "text" ? "" : type == "boolean" ? false : 0;
        if (!Regex.IsMatch(name, @"^[_\p{L}][_\p{L}\p{N}]*$") || types.ContainsKey(name))
            throw new InvalidDataException($"共享变量名称无效或重复: {name}");
        values[name] = ConvertVariable(initial, type, name); types[name] = type;
    }

    public HostExecutionContext(Func<HostEvent, Task> publish, CancellationToken cancellation = default,
        HostExecutionControl? control = null)
    { _publish = publish; _cancellation = cancellation; _control = control ?? new HostExecutionControl(); }

    internal IDisposable EnterSequence(OpenTapPlan plan, IReadOnlyDictionary<string, object?> arguments)
    {
        var frame = new HostSequenceFrame { SequenceId = plan.SequenceId, SequenceName = plan.SequenceName };
        foreach (var argument in arguments)
            if (!plan.Variables.Any(v => v.Scope == "parameter" && v.Name.Equals(argument.Key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"{plan.SequenceId}: 未声明的运行参数 {argument.Key}");
        foreach (var variable in plan.Variables)
        {
            if (variable.Scope is not ("local" or "parameter")) throw new InvalidDataException($"变量作用域无效: {variable.Scope}");
            var initial = variable.Scope == "parameter" && arguments.TryGetValue(variable.Name, out var supplied)
                ? supplied : variable.Default is { } value ? JsonValue(value) : null;
            if (initial is null) throw new InvalidDataException($"变量缺少值: {variable.Name}");
            frame.Variables[variable.Name] = ConvertVariable(initial, variable.DataType, variable.Name);
            frame.VariableTypes[variable.Name] = variable.DataType;
        }
        _frames.Push(frame);
        return new Scope(() =>
        {
            if (_frames.Count == 1) FinalValues = Values();
            _frames.Pop();
        });
    }

    private static object ConvertVariable(object value, string type, string name) => SequenceValueOperations.ConvertTo(value, type);

    internal Dictionary<string, object?> Values()
    {
        var values = new Dictionary<string, object?>(SimulationValues, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Channels) values[pair.Key] = pair.Value;
        foreach (var pair in Station) { values["Station." + pair.Key] = pair.Value; values[pair.Key] = pair.Value; }
        foreach (var pair in Globals) { values["Globals." + pair.Key] = pair.Value; values[pair.Key] = pair.Value; }
        foreach (var pair in Frame.Variables) values["Locals." + pair.Key] = pair.Value;
        foreach (var pair in Frame.Variables) values[pair.Key] = pair.Value;
        return values;
    }

    internal void StoreChannel(string channel, object? value)
    {
        Channels[channel] = value;
        var alias = string.Concat(channel.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        Channels[alias] = value;
    }

    internal object? Resolve(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
        {
            var values = Values();
            var expression = text[2..^1].Trim();
            return values.TryGetValue(expression, out var resolved) ? resolved : HostExpression.Value(expression, values);
        }
        return JsonValue(value);
    }

    internal void AssignVariable(string name, object? value)
    {
        if (name.StartsWith("Globals.", StringComparison.OrdinalIgnoreCase)) { AssignShared(name[8..], value, Globals, GlobalTypes); return; }
        if (name.StartsWith("Station.", StringComparison.OrdinalIgnoreCase)) { AssignShared(name[8..], value, Station, StationTypes); return; }
        if (name.StartsWith("Locals.", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        if (!Frame.VariableTypes.TryGetValue(name, out var type)) throw new InvalidDataException($"变量未声明: {name}");
        if (value is null) throw new InvalidDataException($"变量 {name} 不接受空值");
        Frame.Variables[name] = ConvertVariable(value, type, name);
    }

    private void AssignShared(string name, object? value, Dictionary<string, object?> values, Dictionary<string, string> types)
    {
        if (!types.TryGetValue(name, out var type)) throw new InvalidDataException($"共享变量未声明: {name}");
        if (value is null) throw new InvalidDataException($"变量 {name} 不接受空值");
        values[name] = ConvertVariable(value, type, name);
        PublishSharedValues();
    }

    private void PublishSharedValues() => Publish(new HostEvent
    {
        Type = "shared_values", RunId = RunId,
        Payload = JsonSerializer.SerializeToElement(new
        { globals = new Dictionary<string, object?>(Globals), station = new Dictionary<string, object?>(Station) })
    });

    internal static object? JsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => SequenceValueOperations.Unwrap(value)
    };

    internal string Interpolate(string message) => Regex.Replace(message, @"\$\{([^}]+)\}", match =>
        Convert.ToString(HostExpression.Value(match.Value, Values()), CultureInfo.InvariantCulture) ?? "");

    internal void CheckCancellation()
    {
        _cancellation.ThrowIfCancellationRequested();
        _control.Checkpoint(_cancellation, paused => Publish(new HostEvent
        {
            Type = paused ? "run_paused" : "run_resumed", RunId = RunId,
            Status = paused ? "paused" : "running", Message = paused ? "序列已暂停" : "序列继续执行"
        }));
        _cancellation.ThrowIfCancellationRequested();
        if (_deadlines.Any(deadline => _control.ExecutionTime >= deadline)) throw new TimeoutException("步骤执行超时");
    }

    internal IDisposable EnterDeadline(int milliseconds)
    {
        if (milliseconds == 0) return new Scope(() => { });
        var deadline = _control.ExecutionTime.AddMilliseconds(milliseconds);
        _deadlines.Add(deadline);
        return new Scope(() => _deadlines.Remove(deadline));
    }

    internal IDisposable SuspendDeadlines()
    {
        var saved = _deadlines.ToArray();
        _deadlines.Clear();
        return new Scope(() => _deadlines.AddRange(saved));
    }

    internal IDisposable EnterCleanup()
    {
        _cleanupDepth++;
        return new Scope(() => _cleanupDepth--);
    }

    internal void StopBody() => _stopRequested = true;

    internal void Delay(int milliseconds)
    {
        if (milliseconds < 0) throw new InvalidDataException("等待时间不能小于 0");
        var until = _control.ExecutionTime.AddMilliseconds(milliseconds);
        while (true)
        {
            CheckCancellation();
            var remaining = (int)Math.Ceiling((until - _control.ExecutionTime).TotalMilliseconds);
            if (remaining <= 0) return;
            Task.Delay(Math.Min(remaining, 25), _cancellation).GetAwaiter().GetResult();
        }
    }

    internal void Publish(HostEvent value) => _publish(value).GetAwaiter().GetResult();

    internal void PublishSnapshot(Guid stepId)
    {
        if (_frames.Count == 0 || !Steps.TryGetValue(stepId, out var metadata)) return;
        Publish(new HostEvent
        {
            Type = "execution_snapshot", RunId = RunId, SequenceId = metadata.SequenceId, StepId = metadata.StepId,
            Section = metadata.Section, Path = metadata.Path,
            Payload = JsonSerializer.SerializeToElement(new
            {
                call_stack = _frames.Reverse().Select(frame => new { sequence_id = frame.SequenceId, name = frame.SequenceName }).ToArray(),
                loops = _frames.Reverse().SelectMany(frame => frame.Loops.Reverse()).ToArray(),
                variables = new Dictionary<string, object?>(Frame.Variables),
                globals = new Dictionary<string, object?>(Globals), station = new Dictionary<string, object?>(Station),
                channels = new Dictionary<string, object?>(Channels), simulation_values = new Dictionary<string, object?>(SimulationValues)
            })
        });
    }

    internal IDisposable EnterLoopIteration(string description)
    {
        var loops = Frame.Loops;
        loops.Push(description);
        return new Scope(() => loops.Pop());
    }

    private sealed class Scope(Action leave) : IDisposable
    {
        private Action? _leave = leave;
        public void Dispose() => Interlocked.Exchange(ref _leave, null)?.Invoke();
    }
}
