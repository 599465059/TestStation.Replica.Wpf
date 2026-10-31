using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public enum SequenceRunState
{
    Idle,
    Running,
    Paused,
    Passed,
    Failed,
    Stopped
}

public sealed record SequenceLogEntry(DateTimeOffset Timestamp, string Level, string Message);

public sealed record StepRunResult(
    string StepId,
    string Name,
    string Kind,
    string Status,
    string Message,
    string StartedAt,
    string FinishedAt,
    long DurationMs,
    string SequenceId = "",
    string Section = "",
    string StepRunId = "",
    string Path = "");

public sealed record SequenceRunResult(
    string Id,
    string SequenceId,
    string SequenceName,
    string Status,
    string StartedAt,
    string FinishedAt,
    IReadOnlyList<StepRunResult> Steps,
    IReadOnlyDictionary<string, object?> Values,
    string Error,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyDictionary<string, object?> SimulationValues,
    IReadOnlyDictionary<string, object?>? FinalSimulationValues = null);

public sealed record SequencePreflightIssue(string Code, string Message, string StepId = "", string Path = "");

public sealed record SequencePreflightReport(IReadOnlyList<SequencePreflightIssue> Issues)
{
    public IReadOnlyList<SequencePreflightIssue> Errors => Issues;
    public bool IsValid => Errors.Count == 0;
}

public interface IChannelGateway
{
    void Configure(IReadOnlyDictionary<string, object?> values);
    void Write(string channelId, object? value);
    object? Read(string channelId);
    IReadOnlyDictionary<string, object?> Snapshot();
    void Reset();
}

public sealed class SimulatedChannelGateway : IChannelGateway
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Configure(IReadOnlyDictionary<string, object?> values)
    {
        _values.Clear();
        foreach (var pair in values) _values[pair.Key] = pair.Value;
    }

    public void Write(string channelId, object? value) => _values[channelId] = value;

    public object? Read(string channelId) => _values.TryGetValue(channelId, out var value)
        ? value : throw new InvalidDataException($"模拟通道未配置: {channelId}");

    public IReadOnlyDictionary<string, object?> Snapshot() => new Dictionary<string, object?>(_values, StringComparer.OrdinalIgnoreCase);

    public void Reset() => _values.Clear();
}

public static class SequencePreflightService
{
    private static readonly ISequenceStepCatalog StepCatalog = new BuiltInSequenceStepCatalog();

    public static SequencePreflightReport Validate(SequenceDefinition? sequence)
    {
        var issues = new List<SequencePreflightIssue>();
        if (sequence is null)
        {
            issues.Add(new("sequence.missing", "尚未选择测试序列"));
            return new(issues);
        }
        if (string.IsNullOrWhiteSpace(sequence.Id)) issues.Add(new("sequence.id_required", "序列 ID 不能为空"));
        if (string.IsNullOrWhiteSpace(sequence.Name)) issues.Add(new("sequence.name_required", "序列名称不能为空"));
        if (!sequence.SetupSteps.Any() && !sequence.Steps.Any() && !sequence.CleanupSteps.Any())
            issues.Add(new("sequence.empty", "序列中没有任何步骤"));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Inspect(sequence.SetupSteps, "Setup", ids, issues);
        Inspect(sequence.Steps, "Main", ids, issues);
        Inspect(sequence.CleanupSteps, "Cleanup", ids, issues);
        foreach (var variable in sequence.Variables)
        {
            if (string.IsNullOrWhiteSpace(variable.Name)) issues.Add(new("variable.name_required", "变量名称不能为空"));
            if (!SequenceValueOperations.DataTypes.Contains(variable.DataType))
                issues.Add(new("variable.type_invalid", $"变量类型无效: {variable.DataType}"));
            if (!new[] { "local", "parameter" }.Contains(variable.Scope))
                issues.Add(new("variable.scope_invalid", $"变量作用域无效: {variable.Scope}"));
        }
        return new(issues);
    }

    private static void Inspect(List<SequenceStep> steps, string section, HashSet<string> ids, List<SequencePreflightIssue> issues)
    {
        foreach (var step in steps)
        {
            var path = $"{section} / {step.Name}";
            if (string.IsNullOrWhiteSpace(step.Id)) issues.Add(new("step.id_required", $"{path}: 步骤 ID 不能为空"));
            else if (!ids.Add(step.Id)) issues.Add(new("step.id_duplicate", $"步骤 ID 重复: {step.Id}", step.Id, path));
            if (string.IsNullOrWhiteSpace(step.Name)) issues.Add(new("step.name_required", $"{path}: 步骤名称不能为空", step.Id, path));
            if (step.TimeoutMs < 0) issues.Add(new("step.timeout_invalid", $"{path}: 超时不能小于 0", step.Id, path));
            if (step.OnFailure is not ("STOP" or "CONTINUE" or "GOTO_CLEANUP"))
                issues.Add(new("step.on_failure_invalid", $"{path}: 失败动作无效", step.Id, path));
            if (step.OnComplete is not ("STOP" or "CONTINUE" or "GOTO_CLEANUP"))
                issues.Add(new("step.on_complete_invalid", $"{path}: 完成动作无效", step.Id, path));
            if (StepCatalog.Find(step.Kind) is null) issues.Add(new("step.kind_invalid", $"缺少步骤处理器: {step.Kind}", step.Id, path));
            if (step.Kind is not ("if" or "loop" or "while") && step.Children.Count > 0)
                issues.Add(new("step.children_invalid", $"只有 IF、FOR 或 WHILE 可以包含子步骤: {step.Id}", step.Id, path));
            if (step.Kind != "if" && step.ElseChildren.Count > 0)
                issues.Add(new("step.else_invalid", $"只有 IF 可以包含 ELSE: {step.Id}", step.Id, path));
            switch (step.Kind)
            {
                case "set_value":
                    if (!step.Params.ContainsKey("signals")) { Require(step, "channel_id", "SET VALUE 缺少通道", issues); Require(step, "value", "SET VALUE 缺少设置值", issues); }
                    break;
                case "read_value": if (!step.Params.ContainsKey("signals")) Require(step, "channel_id", "GET VALUE 缺少通道", issues); break;
                case "wait": Require(step, "timeout_ms", "WAIT 缺少时间", issues); break;
                case "if": Require(step, "expression", "IF 缺少条件表达式", issues); break;
                case "loop": Require(step, "variable", "FOR 缺少循环变量", issues); break;
                case "while":
                    Require(step, "expression", "WHILE 缺少条件表达式", issues);
                    if (GetInt(step, "max_iterations", 1000) <= 0) issues.Add(new("while.max_iterations_invalid", "WHILE 最大循环次数必须大于 0", step.Id, path));
                    break;
                case "log": Require(step, "message", "LOG 缺少消息", issues); break;
                case "statement": Require(step, "target_variable", "STATEMENT 缺少目标变量", issues); Require(step, "expression", "STATEMENT 缺少表达式", issues); break;
                case "call_sequence": Require(step, "sequence_id", "SEQUENCE CALL 缺少目标序列", issues); break;
                case "assert":
                    var comparator = GetString(step, "comparator", "==");
                    if (!SequenceValueOperations.Comparators.Where(c => c != "NONE").Contains(comparator))
                        issues.Add(new("assert.comparator_invalid", $"ASSERT 比较方式无效: {comparator}", step.Id, path));
                    if (GetString(step, "failure_action", "STOP") is not ("STOP" or "CONTINUE" or "LOG"))
                        issues.Add(new("assert.failure_action_invalid", "ASSERT 失败动作无效", step.Id, path));
                    break;
            }
            Inspect(step.Children, path + " / THEN", ids, issues);
            Inspect(step.ElseChildren, path + " / ELSE", ids, issues);
        }
    }

    private static void Require(SequenceStep step, string key, string message, List<SequencePreflightIssue> issues)
    {
        if (!step.Params.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
            (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
            issues.Add(new($"step.{key}_required", message, step.Id));
    }

    private static string GetString(SequenceStep step, string key, string fallback) =>
        step.Params.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;

    private static int GetInt(SequenceStep step, string key, int fallback) =>
        int.TryParse(GetString(step, key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;
}

public sealed class SequenceExecutionEngine
{
    private readonly object _sync = new();
    private readonly SimulatedChannelGateway _defaultGateway;
    private readonly IChannelGateway _gateway;
    private readonly Func<string, SequenceDefinition?>? _sequenceResolver;
    private readonly List<StepRunResult> _stepResults = [];
    private readonly Stack<ExecutionFrame> _frames = new();
    private Dictionary<string, object?> _variables => _frames.Peek().Variables.Values;
    private SequenceVariableScope _globals = new([], shared: true);
    private SequenceVariableScope _station = new([], shared: true);
    private readonly Stopwatch _executionClock = new();
    private readonly List<TimeSpan> _deadlines = [];
    private bool _stopBody;
    private readonly Dictionary<string, object?> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncManualResetEvent _pauseGate = new(true);
    private CancellationTokenSource? _runCts;
    private SequenceRunState _state = SequenceRunState.Idle;
    private string _currentRunId = "";

    public SequenceExecutionEngine(IChannelGateway? gateway = null, Func<string, SequenceDefinition?>? sequenceResolver = null)
    {
        _defaultGateway = gateway as SimulatedChannelGateway ?? new SimulatedChannelGateway();
        _gateway = gateway ?? _defaultGateway;
        _sequenceResolver = sequenceResolver;
    }

    public SequenceRunState State { get { lock (_sync) return _state; } }
    public string CurrentRunId => _currentRunId;
    public event Action<SequenceRunState>? StateChanged;
    public event Action<string>? CurrentStepChanged;
    public event Action<StepRunResult>? StepChanged;
    public event Action<SequenceLogEntry>? LogRaised;
    public event Action<SequenceRunResult>? Completed;

    public Task PauseAsync()
    {
        lock (_sync) if (_state == SequenceRunState.Running) { _state = SequenceRunState.Paused; _executionClock.Stop(); StateChanged?.Invoke(_state); _pauseGate.Reset(); Log("INFO", "序列已暂停"); }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        lock (_sync) if (_state == SequenceRunState.Paused) { _state = SequenceRunState.Running; _executionClock.Start(); StateChanged?.Invoke(_state); _pauseGate.Set(); Log("INFO", "序列继续执行"); }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_sync) if (_state is SequenceRunState.Running or SequenceRunState.Paused) { _state = SequenceRunState.Stopped; _runCts?.Cancel(); _pauseGate.Set(); StateChanged?.Invoke(_state); Log("WARN", "序列已停止"); }
        return Task.CompletedTask;
    }

    public async Task<SequenceRunResult> RunAsync(
        SequenceDefinition sequence,
        IReadOnlyDictionary<string, object?>? parameterOverrides = null,
        IReadOnlyDictionary<string, object?>? simulationValues = null,
        CancellationToken cancellationToken = default,
        SequenceExecutionEnvironment? environment = null)
    {
        lock (_sync)
        {
            if (_state is SequenceRunState.Running or SequenceRunState.Paused) throw new InvalidOperationException("测试序列正在运行");
            _state = SequenceRunState.Running;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stepResults.Clear(); _frames.Clear(); _channels.Clear(); _deadlines.Clear(); _stopBody = false;
            _pauseGate.Set(); _executionClock.Restart();
            _currentRunId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        }
        var started = DateTimeOffset.UtcNow;
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var normalizedSimulation = simulationValues is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(simulationValues, StringComparer.OrdinalIgnoreCase);
        var error = "";
        var passed = true;
        var initialized = false;
        StateChanged?.Invoke(SequenceRunState.Running);
        try
        {
            var library = ResolveLibrary(sequence);
            var preflight = SequenceLibraryPreflight.Validate(sequence, library);
            if (!preflight.IsValid) throw new InvalidDataException(preflight.Errors[0].Message);
            _globals = new(environment?.GlobalVariables ?? [], environment?.Globals, shared: true);
            _station = new(environment?.StationVariables ?? [], environment?.Station, shared: true);
            var variables = new SequenceVariableScope(sequence.Variables, parameterOverrides);
            _frames.Push(new(variables));
            parameters = new(variables.Values, StringComparer.OrdinalIgnoreCase);
            _gateway.Configure(normalizedSimulation);
            initialized = true;
            Log("INFO", $"开始执行序列: {sequence.Name}");
            var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sequence.Id };
            var setup = await ExecuteListAsync(sequence.SetupSteps, "Setup", _runCts.Token, sequence.Id, stack);
            if (!setup) passed = false;
            if (passed && _frames.Peek().Flow == Flow.None && !_stopBody)
                passed = await ExecuteListAsync(sequence.Steps, "Main", _runCts.Token, sequence.Id, stack);
        }
        catch (OperationCanceledException) when (_runCts?.IsCancellationRequested == true)
        {
            passed = false;
            error = "用户停止或运行被取消";
        }
        catch (Exception exception)
        {
            passed = false;
            error = exception.Message;
            Log("ERROR", error);
        }
        try
        {
            if (initialized && !await ExecuteCleanupAsync(sequence, new(StringComparer.OrdinalIgnoreCase) { sequence.Id }))
            {
                passed = false;
                if (error.Length == 0) error = "Cleanup 步骤失败";
            }
        }
        catch (Exception exception) { Log("ERROR", $"Cleanup 执行失败: {exception.Message}"); if (passed) { passed = false; error = exception.Message; } }
        var state = State == SequenceRunState.Stopped ? SequenceRunState.Stopped : passed ? SequenceRunState.Passed : SequenceRunState.Failed;
        _executionClock.Stop();
        if (environment is not null && initialized)
        {
            environment.Globals.Clear(); environment.Station.Clear();
            foreach (var pair in _globals.Values) environment.Globals[pair.Key] = pair.Value;
            foreach (var pair in _station.Values) environment.Station[pair.Key] = pair.Value;
        }
        lock (_sync) _state = state;
        StateChanged?.Invoke(state);
        var result = new SequenceRunResult(_currentRunId, sequence.Id, sequence.Name, state == SequenceRunState.Passed ? "completed" : state.ToString().ToLowerInvariant(), started.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"), _stepResults.ToArray(), MergeValues(), error, parameters, normalizedSimulation, _gateway.Snapshot());
        Log(state == SequenceRunState.Passed ? "INFO" : "ERROR", $"序列结束: {result.Status}");
        Completed?.Invoke(result);
        _runCts?.Dispose();
        _runCts = null;
        return result;
    }

    private async Task<bool> ExecuteListAsync(IReadOnlyList<SequenceStep> steps, string section, CancellationToken token, string sequenceId, HashSet<string> callStack)
    {
        var success = true;
        foreach (var step in steps)
        {
            if (_frames.Peek().Flow != Flow.None || section != "Cleanup" && _stopBody) break;
            await CheckpointAsync(token);
            if (!step.Enabled) { PublishStep(step, "skipped", "步骤已禁用", section, sequenceId, DateTimeOffset.UtcNow, 0); continue; }
            var stepSuccess = await ExecuteStepAsync(step, section, token, sequenceId, callStack);
            if (!stepSuccess)
            {
                success = false;
                if (step.OnFailure == "CONTINUE" || IsContinueFailure(step))
                {
                    success = true;
                    continue;
                }
                if (_frames.Peek().Flow != Flow.None) break;
            }
        }
        return success;
    }

    private async Task<bool> ExecuteStepAsync(SequenceStep step, string section, CancellationToken token, string sequenceId, HashSet<string> callStack)
    {
        await CheckpointAsync(token);
        var startedAt = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        CurrentStepChanged?.Invoke(step.Id);
        Log("INFO", $"开始: {section} / {step.Name}");
        var status = "completed"; var message = "完成"; var success = true;
        var bodyStarted = false;
        var deadlineIndex = _deadlines.Count;
        if (step.TimeoutMs > 0) _deadlines.Add(_executionClock.Elapsed + TimeSpan.FromMilliseconds(step.TimeoutMs));
        try
        {
            if (!string.IsNullOrWhiteSpace(step.Precondition) && !SequenceExpression.Evaluate(step.Precondition, MergeValues()))
            {
                PublishStep(step, "skipped", "前置条件不满足", section, sequenceId, startedAt, ElapsedMs(started));
                return true;
            }
            ExecuteExpressions(step.BeforeExpression);
            bodyStarted = true;
            switch (step.Kind)
            {
                case "initialize": _channels.Clear(); message = "初始化完成"; break;
                case "set_value":
                    if (step.Params.ContainsKey("signals")) { RunSignals(step, false); break; }
                    var setChannel = Required(step, "channel_id"); var setValue = Resolve(Value(step, "value")); _gateway.Write(setChannel, setValue); StoreChannel(setChannel, setValue); message = $"{setChannel} = {setValue}"; break;
                case "read_value":
                    if (step.Params.ContainsKey("signals")) { success = RunSignals(step, true); status = success ? "completed" : "failed"; message = success ? "读取完成" : "读取判定失败"; break; }
                    var readChannel = Required(step, "channel_id"); var readValue = _gateway.Read(readChannel); StoreChannel(readChannel, readValue); message = $"{readChannel} -> {readValue}"; break;
                case "wait": await DelayAsync(NonNegativeInt(step, "timeout_ms"), token); message = $"等待 {NonNegativeInt(step, "timeout_ms")} ms"; break;
                case "assert":
                    var actual = ResolveAssertValue(step); var expected = step.Params.TryGetValue("expected", out var expectedValue) ? Resolve(JsonValue(expectedValue)) : null;
                    success = Compare(actual, expected, StringValue(step, "comparator", "=="), step); status = success ? "passed" : "failed";
                    message = success ? $"ASSERT PASS: {actual}" : $"ASSERT FAIL: {actual}"; break;
                case "if":
                    var condition = SequenceExpression.Evaluate(StringValue(step, "expression", ""), MergeValues());
                    success = await ExecuteListAsync(condition ? step.Children : step.ElseChildren, section, token, sequenceId, callStack); message = $"条件结果: {(condition ? "TRUE" : "FALSE")}"; break;
                case "loop":
                    var variable = Required(step, "variable"); var from = Integer(step, "from", 0); var to = Integer(step, "to", -1); var increment = Integer(step, "step", 1);
                    if (increment == 0) throw new InvalidDataException("FOR 步长不能为 0");
                    var oldValue = _variables.TryGetValue(variable, out var previous) ? previous : null; var hadValue = _variables.ContainsKey(variable); var count = 0;
                    _frames.Peek().LoopDepth++;
                    try
                    {
                        for (long index = from; increment > 0 ? index <= to : index >= to; index += increment)
                        {
                            await CheckpointAsync(token);
                            if (++count > Integer(step, "max_iterations", 1000)) throw new InvalidDataException("FOR 循环次数超过最大次数");
                            _variables[variable] = index;
                            if (!await ExecuteListAsync(step.Children, section, token, sequenceId, callStack)) success = false;
                            if (EndIteration()) break;
                        }
                    }
                    finally { _frames.Peek().LoopDepth--; if (hadValue) _variables[variable] = oldValue; else _variables.Remove(variable); }
                    message = $"FOR {variable}: {count} 次"; break;
                case "while":
                    var maxIterations = Integer(step, "max_iterations", 1000);
                    if (maxIterations <= 0) throw new InvalidDataException("WHILE 最大循环次数必须大于 0");
                    var whileCount = 0;
                    _frames.Peek().LoopDepth++;
                    try
                    {
                        while (SequenceExpression.Evaluate(StringValue(step, "expression", ""), MergeValues()))
                        {
                            await CheckpointAsync(token);
                            if (++whileCount > maxIterations) throw new InvalidDataException($"WHILE 循环次数超过 {maxIterations}");
                            if (!await ExecuteListAsync(step.Children, section, token, sequenceId, callStack)) success = false;
                            if (EndIteration()) break;
                        }
                    }
                    finally { _frames.Peek().LoopDepth--; }
                    message = $"WHILE: {whileCount} 次"; break;
                case "break": SetLoopFlow(Flow.Break); break;
                case "continue": SetLoopFlow(Flow.Continue); break;
                case "log":
                    var level = StringValue(step, "level", "INFO").ToUpperInvariant();
                    Log(level, System.Text.RegularExpressions.Regex.Replace(StringValue(step, "message", ""), @"\$\{([^}]+)\}",
                        match => SequenceValueOperations.Display(SequenceExpression.EvaluateValue(match.Value, MergeValues()))));
                    message = StringValue(step, "message", ""); break;
                case "statement":
                    var targetVariable = Required(step, "target_variable");
                    AssignVariable(targetVariable, SequenceExpression.EvaluateValue(StringValue(step, "expression", ""), MergeValues()));
                    message = $"已更新变量: {targetVariable}"; break;
                case "call_sequence":
                    var targetId = Required(step, "sequence_id");
                    if (_sequenceResolver is null) throw new InvalidDataException("未配置序列调用解析器");
                    if (!callStack.Add(targetId)) throw new InvalidDataException($"序列调用形成递归: {targetId}");
                    try
                    {
                        var target = _sequenceResolver(targetId) ?? throw new InvalidDataException($"目标序列不存在: {targetId}");
                        if (target.IsMain) throw new InvalidDataException($"不能调用主序列: {targetId}");
                        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        if (step.Params.TryGetValue("arguments", out var supplied))
                            foreach (var pair in supplied.EnumerateObject()) arguments.Add(pair.Name, Resolve(JsonValue(pair.Value)));
                        _frames.Push(new(new(target.Variables, arguments)));
                        try
                        {
                            try
                            {
                                success = await ExecuteListAsync(target.SetupSteps, "Setup", token, target.Id, callStack);
                                if (_frames.Peek().Flow == Flow.None && !_stopBody)
                                    success = await ExecuteListAsync(target.Steps, "Main", token, target.Id, callStack) && success;
                            }
                            finally { success = await ExecuteCleanupAsync(target, callStack) && success; }
                        }
                        finally { _frames.Pop(); }
                        message = $"SEQUENCE CALL {target.Name}";
                    }
                    finally { callStack.Remove(targetId); }
                    break;
                case "comment": message = step.Description.Length == 0 ? "备注" : step.Description; break;
                default: throw new InvalidDataException($"不支持的步骤类型: {step.Kind}");
            }
            await CheckpointAsync(token);
            if (!success && !string.IsNullOrWhiteSpace(step.FailureMessage)) message = step.FailureMessage;
            if (!success) Log("WARN", message);
        }
        catch (OperationCanceledException)
        {
            status = "stopped"; message = "运行已取消";
            PublishStep(step, status, message, section, sequenceId, startedAt, ElapsedMs(started));
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            success = false; status = "failed"; message = string.IsNullOrWhiteSpace(step.FailureMessage) ? exception.Message : step.FailureMessage; Log("ERROR", message);
        }
        finally
        {
            try
            {
                if (bodyStarted && !token.IsCancellationRequested)
                {
                    try { ExecuteExpressions(step.AfterExpression); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { success = false; status = "failed"; message = $"执行后表达式失败: {error.Message}"; }
                }
            }
            finally { if (step.TimeoutMs > 0) _deadlines.RemoveAt(deadlineIndex); }
        }
        if (success && status != "failed") ApplyAction(step.OnComplete);
        else ApplyAction(IsContinueFailure(step) ? "CONTINUE" : step.OnFailure);
        PublishStep(step, success ? status : "failed", message, section, sequenceId, startedAt, ElapsedMs(started));
        return success;
    }

    private bool IsContinueFailure(SequenceStep step) => step.Kind == "assert" && StringValue(step, "failure_action", "STOP") is "CONTINUE" or "LOG";
    private void PublishStep(SequenceStep step, string status, string message, string section, string sequenceId, DateTimeOffset startedAt, long duration)
    {
        var result = new StepRunResult(step.Id, step.Name, step.Kind, status, message,
            startedAt.ToString("O"), DateTimeOffset.UtcNow.ToString("O"), duration, sequenceId, section);
        _stepResults.Add(result);
        StepChanged?.Invoke(result);
    }
    private void Log(string level, string message) => LogRaised?.Invoke(new(DateTimeOffset.UtcNow, level, message));
    private long ElapsedMs(long started) => (long)(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    private async Task DelayAsync(int milliseconds, CancellationToken token)
    {
        var until = _executionClock.Elapsed + TimeSpan.FromMilliseconds(milliseconds);
        while (true)
        {
            await CheckpointAsync(token);
            var remaining = (int)Math.Ceiling((until - _executionClock.Elapsed).TotalMilliseconds);
            if (remaining <= 0) return;
            await Task.Delay(Math.Min(remaining, 25), token);
        }
    }
    private string Required(SequenceStep step, string key) { var value = StringValue(step, key, "").Trim(); if (value.Length == 0) throw new InvalidDataException($"{step.Name} 缺少 {key}"); return value; }
    private static object? Value(SequenceStep step, string key) => step.Params.TryGetValue(key, out var value) ? JsonValue(value) : null;
    private static string StringValue(SequenceStep step, string key, string fallback) => step.Params.TryGetValue(key, out var value) ? JsonValue(value)?.ToString() ?? fallback : fallback;
    private static int NonNegativeInt(SequenceStep step, string key) { var number = Integer(step, key, 0); if (number < 0) throw new InvalidDataException($"{key} 不能小于 0"); return number; }
    private static int Integer(SequenceStep step, string key, int fallback) { var value = StringValue(step, key, fallback.ToString(CultureInfo.InvariantCulture)); return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : throw new InvalidDataException($"{key} 必须是整数"); }
    private object? Resolve(object? value) => value is string text && text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}')
        ? SequenceExpression.EvaluateValue(text, MergeValues()) : value;
    private void StoreChannel(string channel, object? value) { _channels[channel] = value; _channels[Alias(channel)] = value; }
    private static string Alias(string channel) => string.Concat(channel.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    private object? ResolveAssertValue(SequenceStep step)
    {
        var channel = StringValue(step, "channel_id", "");
        if (channel.Length > 0)
            return _channels.TryGetValue(channel, out var value) ? value : throw new InvalidDataException($"ASSERT 通道尚未读取: {channel}");
        return _channels.Count > 0 ? _channels.Values.Last() : throw new InvalidDataException("ASSERT 没有可比较的实际值");
    }
    private bool Compare(object? actual, object? expected, string comparator, SequenceStep step) => SequenceValueOperations.Compare(
        actual, expected, comparator, StringValue(step, "array_mode", "ALL"), Resolve(Value(step, "minimum")), Resolve(Value(step, "maximum")),
        comparator is "ABS_TOL" or "REL_TOL" ? SequenceValueOperations.Number(Resolve(Value(step, "tolerance"))) : 0);
    private static object? JsonValue(JsonElement value) => SequenceValueOperations.Unwrap(value);

    private Dictionary<string, object?> MergeValues()
    {
        var values = new Dictionary<string, object?>(_gateway.Snapshot(), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _channels) values[pair.Key] = pair.Value;
        foreach (var pair in _station.Values) { values["Station." + pair.Key] = pair.Value; values[pair.Key] = pair.Value; }
        foreach (var pair in _globals.Values) { values["Globals." + pair.Key] = pair.Value; values[pair.Key] = pair.Value; }
        if (_frames.Count > 0)
            foreach (var pair in _variables) { values[pair.Key] = pair.Value; values["Locals." + pair.Key] = pair.Value; }
        return values;
    }

    private IReadOnlyList<SequenceDefinition> ResolveLibrary(SequenceDefinition root)
    {
        var library = new Dictionary<string, SequenceDefinition>(StringComparer.OrdinalIgnoreCase);
        void Visit(SequenceDefinition sequence)
        {
            if (library.ContainsKey(sequence.Id)) return;
            if (library.Count >= 1024) throw new InvalidDataException("序列调用库超过 1024 个序列");
            library.Add(sequence.Id, sequence);
            foreach (var step in SequenceEditor.Walk(sequence).Where(s => s.Kind == "call_sequence"))
                if (_sequenceResolver?.Invoke(StringValue(step, "sequence_id", "")) is { } target) Visit(target);
        }
        Visit(root);
        return library.Values.ToArray();
    }

    private async Task<bool> ExecuteCleanupAsync(SequenceDefinition sequence, HashSet<string> stack)
    {
        var saved = _deadlines.ToArray(); _deadlines.Clear();
        _frames.Peek().Flow = Flow.None;
        try { return await ExecuteListAsync(sequence.CleanupSteps, "Cleanup", CancellationToken.None, sequence.Id, stack); }
        finally { _deadlines.Clear(); _deadlines.AddRange(saved); }
    }

    private async Task CheckpointAsync(CancellationToken token)
    {
        await _pauseGate.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        CheckDeadline();
    }

    private void CheckDeadline()
    {
        if (_deadlines.Any(deadline => _executionClock.Elapsed >= deadline)) throw new TimeoutException("步骤执行超时");
    }

    private void ExecuteExpressions(string source)
    {
        foreach (var assignment in SequenceExpressionProgram.Parse(source))
        {
            CheckDeadline();
            AssignVariable(assignment.Target, SequenceExpression.EvaluateValue(assignment.Expression, MergeValues()));
        }
    }

    private void AssignVariable(string name, object? value)
    {
        if (name.StartsWith("Globals.", StringComparison.OrdinalIgnoreCase)) _globals.Assign(name[8..], value);
        else if (name.StartsWith("Station.", StringComparison.OrdinalIgnoreCase)) _station.Assign(name[8..], value);
        else _frames.Peek().Variables.Assign(name.StartsWith("Locals.", StringComparison.OrdinalIgnoreCase) ? name[7..] : name, value);
    }

    private bool RunSignals(SequenceStep step, bool read)
    {
        var rows = SequenceSignalOperation.Read(step.Params["signals"]);
        foreach (var row in rows) row.Validate(read);
        var passed = true;
        foreach (var row in rows)
        {
            CheckDeadline();
            if (!read)
            {
                var value = SequenceValueOperations.ReadInput(row.Value, row.DataType, row.Mode, MergeValues());
                _gateway.Write(row.ChannelId, value); StoreChannel(row.ChannelId, value); continue;
            }
            var actual = SequenceValueOperations.ConvertTo(_gateway.Read(row.ChannelId), row.DataType);
            StoreChannel(row.ChannelId, actual);
            if (!string.IsNullOrWhiteSpace(row.Value)) AssignVariable(row.Value.Trim(), actual);
            if (row.Comparator == "NONE") continue;
            var values = MergeValues();
            var range = row.Comparator is "IN_RANGE" or "OUTSIDE_RANGE";
            var result = SequenceValueOperations.Compare(actual, range ? null : row.RuleInput(row.Expected, values), row.Comparator, row.ArrayMode,
                range ? row.RuleInput(row.Minimum, values) : null, range ? row.RuleInput(row.Maximum, values) : null,
                row.Comparator is "ABS_TOL" or "REL_TOL" ? SequenceValueOperations.Number(row.Mode == "expression"
                    ? SequenceExpression.EvaluateValue(row.Tolerance, values) : row.Tolerance) : 0);
            if (!result) { passed = false; Log("ERROR", $"读取判定失败: {row.ChannelId}"); }
        }
        return passed;
    }

    private void ApplyAction(string action)
    {
        if (action == "GOTO_CLEANUP") _frames.Peek().Flow = Flow.Cleanup;
        else if (action == "STOP") { _frames.Peek().Flow = Flow.Stop; _stopBody = true; }
    }

    private void SetLoopFlow(Flow flow)
    {
        if (_frames.Peek().LoopDepth == 0) throw new InvalidDataException("BREAK/CONTINUE 只能用于当前序列的循环");
        _frames.Peek().Flow = flow;
    }

    private bool EndIteration()
    {
        var flow = _frames.Peek().Flow;
        if (flow is Flow.Break or Flow.Continue) _frames.Peek().Flow = Flow.None;
        return _stopBody || flow is Flow.Break or Flow.Stop or Flow.Cleanup;
    }

    private enum Flow { None, Break, Continue, Stop, Cleanup }
    private sealed class ExecutionFrame(SequenceVariableScope variables)
    {
        public SequenceVariableScope Variables { get; } = variables;
        public Flow Flow { get; set; }
        public int LoopDepth { get; set; }
    }
}

public sealed class AsyncManualResetEvent(bool initialState)
{
    private TaskCompletionSource<bool> _source = CreateSource(initialState);
    public Task WaitAsync(CancellationToken token) => _source.Task.WaitAsync(token);
    public void Set() => _source.TrySetResult(true);
    public void Reset() { while (true) { var current = _source; if (!current.Task.IsCompleted || Interlocked.CompareExchange(ref _source, CreateSource(false), current) == current) return; } }
    private static TaskCompletionSource<bool> CreateSource(bool complete) { var source = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); if (complete) source.SetResult(true); return source; }
}
