using System.IO;
using TestStation.Application;

namespace TestStation.App.Services;

public interface ISequenceReportService
{
    IReadOnlyList<RunReportSummary> LoadSummaries(string projectRoot);
    SequenceRunResult? LoadResult(string projectRoot, string runId);
    string EventsPath(string projectRoot, string runId);
}

public sealed class SequenceReportService : ISequenceReportService
{
    public IReadOnlyList<RunReportSummary> LoadSummaries(string projectRoot) =>
        new RunResultRepository(projectRoot).LoadSummaries();

    public SequenceRunResult? LoadResult(string projectRoot, string runId) =>
        new RunResultRepository(projectRoot).LoadResult(runId);

    public string EventsPath(string projectRoot, string runId) =>
        Path.Combine(projectRoot, "reports", runId + ".opentap.json");
}
