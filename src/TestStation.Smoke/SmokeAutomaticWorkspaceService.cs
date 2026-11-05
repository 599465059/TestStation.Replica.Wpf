using System.Text.Json;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.Smoke;

/// <summary>Keep UI test station variables in memory, away from the user's station configuration.</summary>
internal sealed class SmokeAutomaticWorkspaceService : IAutomaticWorkspaceService
{
    private readonly AutomaticWorkspaceService _inner = new();
    private List<SequenceVariable> _station =
    [new() { Name = "UiStation", Scope = "station", DataType = "text", Default = JsonSerializer.SerializeToElement("UI-SMOKE") }];

    public AutomaticWorkspaceSnapshot Load(ProjectWorkspace workspace) => _inner.Load(workspace) with
    { StationVariables = Clone(_station) };

    public void Save(ProjectWorkspace workspace, IEnumerable<SequenceSubproject> subprojects, IEnumerable<SequenceVariable> stationVariables)
    {
        new SequenceWorkspaceRepository(workspace).Save(subprojects);
        _station = Clone(stationVariables);
        workspace.TouchUpdatedAt();
    }

    private static List<SequenceVariable> Clone(IEnumerable<SequenceVariable> variables) =>
        JsonSerializer.Deserialize<List<SequenceVariable>>(JsonSerializer.Serialize(variables))!;
    public IReadOnlyList<SimulationScenario> LoadScenarios(ProjectWorkspace workspace) => _inner.LoadScenarios(workspace);
    public void SaveScenarios(ProjectWorkspace workspace, IReadOnlyList<SimulationScenario> scenarios) => _inner.SaveScenarios(workspace, scenarios);
    public IReadOnlyDictionary<string, JsonElement> LoadSimulation(ProjectWorkspace workspace) => _inner.LoadSimulation(workspace);
    public IReadOnlyList<SequenceChannelDefinition> LoadChannelDefinitions(ProjectWorkspace workspace) => _inner.LoadChannelDefinitions(workspace);
    public void SaveSimulation(ProjectWorkspace workspace, IReadOnlyDictionary<string, JsonElement> values, bool preserveDefinitions = false) =>
        _inner.SaveSimulation(workspace, values, preserveDefinitions);

    public static IReadOnlyList<SequenceDefinition> LoadSequences(ProjectWorkspace workspace) =>
        new SequenceWorkspaceRepository(workspace).Load().SelectMany(project => project.Sequences).ToArray();
}
