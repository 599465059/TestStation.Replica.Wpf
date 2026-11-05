using System.Collections.Concurrent;
using System.Text.Json;
using OpenTap;

namespace TestStation.OpenTapHost;

public sealed class HostResultListener : ResultListener
{
    private readonly HostExecutionContext _context;
    private readonly ConcurrentDictionary<Guid, HostStepMetadata> _runs = new();

    public HostResultListener(HostExecutionContext context)
    { _context = context; Name = "TestStation Host Results"; }

    public override void OnTestPlanRunStart(TestPlanRun planRun) => _context.Publish(new HostEvent
    {
        Type = "run_started", RunId = _context.RunId, Status = "running", Message = planRun.TestPlanName,
        Payload = JsonSerializer.SerializeToElement(new { opentap_run_id = planRun.Id })
    });

    public override void OnTestStepRunStart(TestStepRun stepRun)
    {
        if (!_context.Steps.TryGetValue(stepRun.TestStepId, out var metadata)) return;
        _runs[stepRun.Id] = metadata;
        _context.Publish(StepEvent("step_started", stepRun, metadata, "running", stepRun.TestStepName));
    }

    public override void OnTestStepRunCompleted(TestStepRun stepRun)
    {
        if (!_context.Steps.TryGetValue(stepRun.TestStepId, out var metadata)) return;
        _context.Outcomes.TryRemove(stepRun.Id, out var outcome);
        // Steps interrupted by Stop report "stopped"; CLEANUP steps that ran after Stop keep their real result.
        var interrupted = _context.WasStopped && (stepRun.Exception is OperationCanceledException || stepRun.Verdict == Verdict.Aborted);
        var status = interrupted ? "stopped" : outcome?.Status is { Length: > 0 } state ? state : State(stepRun.Verdict);
        var message = outcome?.Message is { Length: > 0 } failure ? failure : stepRun.Exception?.Message ?? stepRun.TestStepName;
        var result = StepEvent("step_finished", stepRun, metadata, status, message);
        if (metadata.RecordResult) _context.RecordedSteps.Enqueue(result);
        _context.Publish(result);
    }

    public override void OnTestPlanRunCompleted(TestPlanRun planRun, Stream logStream) => _context.Publish(new HostEvent
    {
        Type = "run_finished", RunId = _context.RunId,
        Status = _context.WasStopped ? "stopped" : State(planRun.Verdict),
        Message = planRun.Exception?.Message ?? "OpenTAP 执行完成",
        Payload = JsonSerializer.SerializeToElement(new
        {
            opentap_run_id = planRun.Id, duration_ms = planRun.Duration.TotalMilliseconds,
            results = _context.RecordedSteps.ToArray(), values = _context.FinalValues,
            globals = new Dictionary<string, object?>(_context.Globals), station = new Dictionary<string, object?>(_context.Station),
            simulation_values = new Dictionary<string, object?>(_context.SimulationValues)
        })
    });

    public override void OnResultPublished(Guid stepRunId, ResultTable result)
    {
        if (!_runs.TryGetValue(stepRunId, out var metadata) || !metadata.RecordResult) return;
        _context.Publish(new HostEvent
        {
            Type = "result", RunId = _context.RunId, StepRunId = stepRunId.ToString(),
            StepId = metadata.StepId, SequenceId = metadata.SequenceId, Section = metadata.Section, Path = metadata.Path,
            Payload = JsonSerializer.SerializeToElement(new
            {
                name = result.Name, rows = result.Rows,
                columns = result.Columns.Select(column => new { name = column.Name, data = column.Data }).ToArray()
            })
        });
    }

    private HostEvent StepEvent(string type, TestStepRun run, HostStepMetadata metadata, string status, string message) => new()
    {
        Type = type, RunId = _context.RunId, StepId = metadata.StepId, SequenceId = metadata.SequenceId,
        StepRunId = run.Id.ToString(), ParentStepRunId = run.Parent.ToString(),
        Section = metadata.Section, Path = metadata.Path, Status = status, Message = message,
        Payload = JsonSerializer.SerializeToElement(new
        {
            opentap_step_id = run.TestStepId, started_at = run.StartTime.ToUniversalTime(),
            duration_ms = run.Duration.TotalMilliseconds, record_result = metadata.RecordResult
        })
    };

    private static string State(Verdict verdict) => verdict switch
    {
        Verdict.Pass => "passed", Verdict.Fail or Verdict.Error => "failed",
        Verdict.Inconclusive => "inconclusive", _ => "skipped"
    };
}
