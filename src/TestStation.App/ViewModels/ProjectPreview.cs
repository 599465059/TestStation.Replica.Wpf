using System.IO;
using TestStation.App.Localization;
using TestStation.Application;

namespace TestStation.App.ViewModels;

/// <summary>
/// Read-only summary of a project folder for the start page, so the operator sees what a recent
/// project contains (and which device families it needs) before opening it. Never writes files;
/// unreadable parts are reported in <see cref="Problem"/> instead of throwing.
/// </summary>
public sealed record ProjectPreview(string Name, string Path, int SequenceCount, string MainSequences,
    int DeviceCount, int ResourceCount, string Families, string LastRun, string LastRunKind, string Problem)
{
    public bool HasProblem => Problem.Length > 0;

    public static ProjectPreview Load(string path, Func<string, string>? familyName = null)
    {
        string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), problem = "";
        int sequences = 0, devices = 0, resources = 0;
        string mains = UiText.Get("WorkbenchNone"), families = UiText.Get("WorkbenchNone"), lastRun = UiText.Get("PreviewNoRuns"), runKind = "idle";
        try
        {
            var workspace = ProjectWorkspace.Open(path);
            name = workspace.Name;
            var subprojects = new SequenceWorkspaceRepository(workspace).Load();
            sequences = subprojects.Sum(p => p.Sequences.Count);
            var names = subprojects.Select(p => p.Sequences.FirstOrDefault(s => s.IsMain)?.Name).OfType<string>().ToArray();
            if (names.Length > 0) mains = string.Join("、", names);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            problem = error.Message;
        }
        try
        {
            var catalog = new TestStation.Infrastructure.Resources.ResourceCatalogRepository().Load(path).Catalog;
            devices = catalog.Devices.Count; resources = catalog.Transports.Count;
            var ids = catalog.Devices.Select(d => d.FamilyId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (ids.Length > 0) families = string.Join("、", ids.Select(id => familyName?.Invoke(id) ?? id));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { problem = problem.Length > 0 ? problem : error.Message; }
        try
        {
            if (new RunResultRepository(path).LoadSummaries().FirstOrDefault() is { } run)
            {
                lastRun = $"{UiDisplay.State(run.Status)}，{run.StartedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
                runKind = run.Status switch { "completed" => "success", "failed" => "error", _ => "idle" };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return new(name, path, sequences, mains, devices, resources, families, lastRun, runKind, problem);
    }
}
