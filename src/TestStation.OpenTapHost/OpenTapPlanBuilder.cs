using System.Globalization;
using System.Text.Json;
using OpenTap;
using TestStation.Application;

namespace TestStation.OpenTapHost;

public static class OpenTapPlanBuilder
{
    public static TestPlan Build(OpenTapPlan plan, HostExecutionContext context,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var testPlan = new TestPlan { PrintTestPlanRunSummary = false };
        // A sequence is a composite step so Cleanup can run even when its body fails.
        testPlan.Steps.Add(BuildSequence(plan, context, arguments ?? new Dictionary<string, object?>(), plan.SequenceId));
        return testPlan;
    }

    internal static OpenTapSequenceStep BuildSequence(OpenTapPlan plan, HostExecutionContext context,
        IReadOnlyDictionary<string, object?> arguments, string path)
    {
        var sequence = new OpenTapSequenceStep(plan, context, arguments);
        foreach (var section in plan.Sections)
            foreach (var step in section.Steps)
            {
                var child = BuildStep(step, section.Name, plan.SequenceId, path, context);
                sequence.ChildTestSteps.Add(child);
                sequence.Sections[section.Name].Add(child);
            }
        return sequence;
    }

    private static OpenTapBridgeStep BuildStep(OpenTapPlanStep step, string section, string sequenceId,
        string path, HostExecutionContext context)
    {
        var stepPath = $"{path}/{section}/{step.Id}";
        var bridge = new OpenTapBridgeStep(step, context);
        context.Steps.Add(bridge.Id, new HostStepMetadata(step.Id, sequenceId, section, stepPath, step.RecordResult));
        foreach (var child in step.Children)
        {
            var compiled = BuildStep(child, section, sequenceId, stepPath + "/children", context);
            bridge.ChildTestSteps.Add(compiled);
            bridge.ThenSteps.Add(compiled);
        }
        foreach (var child in step.ElseChildren)
        {
            var compiled = BuildStep(child, section, sequenceId, stepPath + "/else", context);
            bridge.ChildTestSteps.Add(compiled);
            bridge.ElseSteps.Add(compiled);
        }
        if (step.CalledSequence is { } called)
        {
            var sequence = BuildSequence(called, context, new Dictionary<string, object?>(), stepPath + "/" + called.SequenceId);
            bridge.ChildTestSteps.Add(sequence);
            bridge.CalledSequence = sequence;
        }
        return bridge;
    }
}

public abstract class HostCompositeStep : TestStep
{
    protected readonly HostExecutionContext Context;

    protected HostCompositeStep(HostExecutionContext context)
    {
        Context = context;
        // Host control flow handles failures explicitly; do not inherit station break settings.
        BreakConditionProperty.SetBreakCondition(this, (BreakCondition)0);
    }

    /// <param name="continueAfterFailure">CLEANUP: a failed step must not skip the remaining
    /// cleanup steps (for example "turn output off" after a failed reading).</param>
    protected void RunSteps(IEnumerable<OpenTapBridgeStep> steps, bool continueAfterFailure = false)
    {
        foreach (var step in steps)
        {
            Context.CheckCancellation();
            if (continueAfterFailure && Context.Frame.Flow is HostFlow.Stop or HostFlow.Cleanup) Context.Frame.Flow = HostFlow.None;
            if (Context.Frame.Flow != HostFlow.None || Context.ShouldStopBody) break;
            if (!step.Enabled) { step.PublishSkipped("步骤已禁用"); continue; }
            // Run each selected child through OpenTAP, and wait before inspecting the Host directive.
            var run = RunChildStep(step, false, Array.Empty<ResultParameter>());
            run.WaitForCompletion();
            UpgradeVerdict(run.Verdict);
            if (run.Exception is not null) step.HandleFailure(run.Exception.Message, Verdict.Error);
        }
    }
}

[AllowChildrenOfType(typeof(OpenTapBridgeStep))]
public sealed class OpenTapSequenceStep : HostCompositeStep
{
    private readonly OpenTapPlan _plan;
    internal IReadOnlyDictionary<string, object?> Arguments { get; set; }
    internal Dictionary<string, List<OpenTapBridgeStep>> Sections { get; } = new()
    { ["Setup"] = [], ["Main"] = [], ["Cleanup"] = [] };

    public OpenTapSequenceStep(OpenTapPlan plan, HostExecutionContext context,
        IReadOnlyDictionary<string, object?> arguments) : base(context)
    { _plan = plan; Arguments = arguments; Name = plan.SequenceName; }

    public override void Run()
    {
        using var scope = Context.EnterSequence(_plan, Arguments);
        try
        {
            RunSteps(Sections["Setup"]);
            if (Context.Frame.Flow == HostFlow.None) RunSteps(Sections["Main"]);
        }
        finally
        {
            // STOP and GOTO_CLEANUP both end the current body. Cleanup runs once at this boundary.
            Context.Frame.Flow = HostFlow.None;
            using var deadlines = Context.SuspendDeadlines();
            using var cleanup = Context.EnterCleanup();
            RunSteps(Sections["Cleanup"], continueAfterFailure: true);
            if (Context.Frame.Flow is HostFlow.Stop or HostFlow.Cleanup) Context.Frame.Flow = HostFlow.None;
        }
        UpgradeVerdict(Verdict.Pass);
    }
}

[AllowChildrenOfType(typeof(HostCompositeStep))]
public sealed partial class OpenTapBridgeStep : HostCompositeStep
{
    private static readonly OpenTap.TraceSource Trace = OpenTap.Log.CreateSource("TestStation.Sequence");
    private readonly OpenTapPlanStep _step;
    internal List<OpenTapBridgeStep> ThenSteps { get; } = [];
    internal List<OpenTapBridgeStep> ElseSteps { get; } = [];
    internal OpenTapSequenceStep? CalledSequence { get; set; }
    internal string Outcome { get; private set; } = "";
    internal string Failure { get; private set; } = "";

    public OpenTapBridgeStep(OpenTapPlanStep step, HostExecutionContext context) : base(context)
    { _step = step; Name = step.Name; Enabled = step.Enabled; }

    public override void Run()
    {
        Outcome = "";
        Failure = "";
        using var deadline = Context.EnterDeadline(_step.TimeoutMs);
        var bodyStarted = false;
        try
        {
            Context.PublishSnapshot(Id);
            Context.CheckCancellation();
            if (_step.Precondition.Length > 0 && !HostExpression.Evaluate(_step.Precondition, Context.Values()))
            { Outcome = "skipped"; return; }
            ExecuteExpressions(_step.BeforeExpression);
            bodyStarted = true;
            switch (_step.Kind)
            {
                case "initialize": Context.Channels.Clear(); break;
                case "wait": Context.Delay(ReadInt("timeout_ms", 0)); break;
                case "if":
                    RunSteps(HostExpression.Evaluate(ReadString("expression"), Context.Values()) ? ThenSteps : ElseSteps);
                    break;
                case "loop": RunFor(); break;
                case "while": RunWhile(); break;
                case "break": SetLoopFlow(HostFlow.Break); break;
                case "continue": SetLoopFlow(HostFlow.Continue); break;
                case "statement":
                    var variable = ReadString("target_variable");
                    if (string.IsNullOrWhiteSpace(variable)) throw new InvalidDataException("目标变量不能为空");
                    Context.AssignVariable(variable, HostExpression.Value(ReadString("expression"), Context.Values()));
                    break;
                case "call_sequence": RunSequence(); break;
                case "log": PublishLog(); break;
                case "set_value": RunSignals(false); break;
                case "read_value": RunSignals(true); break;
                case "assert":
                    if (!Compare()) HandleFailure(Failure.Length > 0 ? Failure : "断言失败", Verdict.Fail);
                    break;
                case "comment": break;
                default: throw new InvalidDataException($"未实现的步骤: {_step.Kind}");
            }
            Context.CheckCancellation();
            UpgradeVerdict(Verdict.Pass);
            ApplyCompletionAction();
        }
        catch (OperationCanceledException) when (Context.WasStopped) { throw; }
        catch (Exception error) { HandleFailure(error.Message, Verdict.Error); }
        finally
        {
            if (bodyStarted && (!Context.WasStopped || Context.InCleanup))
            {
                try { ExecuteExpressions(_step.AfterExpression); }
                catch (OperationCanceledException) when (Context.WasStopped) { throw; }
                catch (Exception error) { HandleFailure($"执行后表达式失败: {error.Message}", Verdict.Error); }
            }
            if (StepRun is not null) Context.Outcomes[StepRun.Id] = new HostStepOutcome(Outcome, Failure);
            Context.PublishSnapshot(Id);
        }
    }

    private void RunFor()
    {
        var variable = ReadString("variable");
        if (string.IsNullOrWhiteSpace(variable)) throw new InvalidDataException("FOR 缺少循环变量");
        var from = ReadInt("from", 0);
        var to = ReadInt("to", -1);
        var increment = ReadInt("step", 1);
        var max = ReadInt("max_iterations", 1000);
        if (increment == 0 || max <= 0) throw new InvalidDataException("FOR 步长不能为 0，最大次数必须大于 0");
        var frame = Context.Frame;
        var hadPrevious = frame.Variables.TryGetValue(variable, out var previous);
        frame.LoopDepth++;
        try
        {
            var count = 0;
            for (long index = from; increment > 0 ? index <= to : index >= to; index += increment)
            {
                Context.CheckCancellation();
                if (++count > max) throw new InvalidDataException($"FOR 循环次数超过 {max}");
                frame.Variables[variable] = index;
                using var iteration = Context.EnterLoopIteration($"{_step.Name}: 第 {count} 次，{variable}={index}");
                Context.PublishSnapshot(Id);
                RunSteps(ThenSteps);
                if (EndIteration()) break;
            }
        }
        finally
        {
            frame.LoopDepth--;
            if (hadPrevious) frame.Variables[variable] = previous; else frame.Variables.Remove(variable);
        }
    }

    private void RunWhile()
    {
        var max = ReadInt("max_iterations", 1000);
        if (max <= 0) throw new InvalidDataException("WHILE 最大循环次数必须大于 0");
        Context.Frame.LoopDepth++;
        try
        {
            var count = 0;
            while (HostExpression.Evaluate(ReadString("expression"), Context.Values()))
            {
                Context.CheckCancellation();
                if (++count > max) throw new InvalidDataException($"WHILE 循环次数超过 {max}");
                using var iteration = Context.EnterLoopIteration($"{_step.Name}: 第 {count} 次");
                Context.PublishSnapshot(Id);
                RunSteps(ThenSteps);
                if (EndIteration()) break;
            }
        }
        finally { Context.Frame.LoopDepth--; }
    }

    private bool EndIteration()
    {
        var flow = Context.Frame.Flow;
        if (flow is HostFlow.Break or HostFlow.Continue) Context.Frame.Flow = HostFlow.None;
        return Context.ShouldStopBody || flow is HostFlow.Break or HostFlow.Stop or HostFlow.Cleanup;
    }

    private void SetLoopFlow(HostFlow flow)
    {
        if (Context.Frame.LoopDepth == 0) throw new InvalidDataException("BREAK/CONTINUE 只能用于当前序列的循环");
        Context.Frame.Flow = flow;
    }

    private void RunSequence()
    {
        var target = CalledSequence ?? throw new InvalidDataException("子序列未编译");
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (_step.Parameters.TryGetValue("arguments", out var value))
        {
            if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("子序列参数必须是 JSON 对象");
            foreach (var pair in value.EnumerateObject()) arguments[pair.Name] = Context.Resolve(pair.Value);
        }
        target.Arguments = arguments;
        var run = RunChildStep(target, false, Array.Empty<ResultParameter>());
        run.WaitForCompletion();
        UpgradeVerdict(run.Verdict);
        if (run.Exception is not null || run.Verdict is Verdict.Error or Verdict.Fail)
            HandleFailure(run.Exception?.Message ?? "子序列执行失败", run.Verdict);
    }

    internal void HandleFailure(string message, Verdict verdict)
    {
        Failure = _step.FailureMessage.Length > 0 ? _step.FailureMessage : message;
        Outcome = "failed";
        UpgradeVerdict(verdict);
        Context.Frame.Flow = _step.OnFailure switch
        { "CONTINUE" => Context.Frame.Flow, "GOTO_CLEANUP" => HostFlow.Cleanup, _ => HostFlow.Stop };
        if (_step.OnFailure == "STOP") Context.StopBody();
        PublishLog("ERROR", Failure);
    }

    private void ApplyCompletionAction()
    {
        if (Outcome == "failed") return;
        if (_step.OnComplete == "GOTO_CLEANUP") Context.Frame.Flow = HostFlow.Cleanup;
        else if (_step.OnComplete == "STOP") { Context.Frame.Flow = HostFlow.Stop; Context.StopBody(); }
    }

    private bool Compare()
    {
        var channel = ReadString("channel_id");
        if (!Context.Channels.TryGetValue(channel, out var actual)) throw new InvalidDataException($"断言通道不存在: {channel}");
        var comparator = ReadString("comparator", "==");
        if (comparator == "NONE") return true;
        var range = comparator is "IN_RANGE" or "OUTSIDE_RANGE";
        var expected = range ? null : Resolve("expected");
        var minimum = range ? Resolve("minimum") : null;
        var maximum = range ? Resolve("maximum") : null;
        var tolerance = comparator is "ABS_TOL" or "REL_TOL"
            ? Number(Resolve("tolerance")) : 0;
        var passed = SequenceValueOperations.Compare(actual, expected, comparator, ReadString("array_mode", "ALL"), minimum, maximum, tolerance);
        PublishAssertion(channel, actual, comparator, comparator is "IN_RANGE" or "OUTSIDE_RANGE" ? $"{DisplayValue(minimum)} .. {DisplayValue(maximum)}" : expected, passed);
        return passed;
    }

    private void PublishMeasurement(string name, string channel, object? value)
    {
        if (!_step.RecordResult) return;
        Results.Publish(name, new List<string> { "通道", "值" }, new IConvertible[] { channel, value as IConvertible ?? DisplayValue(value) });
    }

    private void PublishAssertion(string channel, object? actual, string comparator, object? expected, bool passed)
    {
        if (!passed) Failure = $"断言失败: {channel}，实际值 {DisplayValue(actual)}，期望 {comparator} {DisplayValue(expected)}";
        if (!_step.RecordResult) return;
        Results.Publish("断言", new List<string> { "通道", "实际值", "比较", "期望值", "通过" },
            new IConvertible[] { channel, DisplayValue(actual), comparator, DisplayValue(expected), passed });
    }

    private static string DisplayValue(object? value) => SequenceValueOperations.Display(value);

    private void PublishLog() => PublishLog(ReadString("level", "INFO"), Context.Interpolate(ReadString("message")));

    private void PublishLog(string level, string message)
    {
        var metadata = Context.Steps[Id];
        Context.Publish(new HostEvent
        {
            Type = "log", RunId = Context.RunId, StepId = _step.Id, Message = message,
            StepRunId = StepRun?.Id.ToString() ?? "", SequenceId = metadata.SequenceId,
            Section = metadata.Section, Path = metadata.Path,
            Payload = JsonSerializer.SerializeToElement(new { level, sequence_id = metadata.SequenceId, path = metadata.Path })
        });
        switch (level.ToUpperInvariant())
        {
            case "ERROR": OpenTap.Log.Error(Trace, "{0}", message); break;
            case "WARN": case "WARNING": OpenTap.Log.Warning(Trace, "{0}", message); break;
            case "DEBUG": OpenTap.Log.Debug(Trace, "{0}", message); break;
            default: OpenTap.Log.Info(Trace, "{0}", message); break;
        }
    }

    internal void PublishSkipped(string message)
    {
        var metadata = Context.Steps[Id];
        var result = new HostEvent
        {
            Type = "step_finished", RunId = Context.RunId, StepId = _step.Id, Status = "skipped", Message = message,
            SequenceId = metadata.SequenceId, Section = metadata.Section, Path = metadata.Path,
            Payload = JsonSerializer.SerializeToElement(new { sequence_id = metadata.SequenceId, section = metadata.Section,
                path = metadata.Path, record_result = metadata.RecordResult, started_at = DateTimeOffset.UtcNow, duration_ms = 0 })
        };
        if (metadata.RecordResult) Context.RecordedSteps.Enqueue(result);
        Context.Publish(result);
    }

    private string ReadString(string key, string fallback = "") =>
        _step.Parameters.TryGetValue(key, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString() : fallback;

    private object? Resolve(string key) => _step.Parameters.TryGetValue(key, out var value) ? Context.Resolve(value) : null;

    private int ReadInt(string key, int fallback)
    {
        if (!_step.Parameters.ContainsKey(key)) return fallback;
        var value = HostExpression.Value(ReadString(key), Context.Values());
        var number = Number(value);
        if (number != Math.Truncate(number) || number < int.MinValue || number > int.MaxValue)
            throw new InvalidDataException($"{key} 必须是整数");
        return (int)number;
    }

    private static double Number(object? value) =>
        double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number : throw new InvalidDataException($"值不是有效数字: {value}");
}
