using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

public sealed record AutomaticWorkspaceSnapshot(
    IReadOnlyList<SequenceSubproject> Subprojects,
    IReadOnlyDictionary<string, JsonElement> SimulationValues,
    IReadOnlyList<SequenceVariable> StationVariables);

/// <summary>Application boundary for automatic-test workspace persistence.</summary>
public interface IAutomaticWorkspaceService
{
    AutomaticWorkspaceSnapshot Load(ProjectWorkspace workspace);
    void Save(ProjectWorkspace workspace, IEnumerable<SequenceSubproject> subprojects,
        IEnumerable<SequenceVariable> stationVariables);
    IReadOnlyList<SimulationScenario> LoadScenarios(ProjectWorkspace workspace);
    void SaveScenarios(ProjectWorkspace workspace, IReadOnlyList<SimulationScenario> scenarios);
    IReadOnlyDictionary<string, JsonElement> LoadSimulation(ProjectWorkspace workspace);
    IReadOnlyList<SequenceChannelDefinition> LoadChannelDefinitions(ProjectWorkspace workspace);
    void SaveSimulation(ProjectWorkspace workspace, IReadOnlyDictionary<string, JsonElement> values,
        bool preserveDefinitions = false);
}

public sealed class AutomaticWorkspaceService : IAutomaticWorkspaceService
{
    public AutomaticWorkspaceSnapshot Load(ProjectWorkspace workspace)
    {
        var repository = new SimulationRepository(workspace.Root);
        var values = repository.Load();
        if (!File.Exists(Path.Combine(workspace.Root, "channels", "simulation.json")))
        {
            values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            {
                ["Sim.Voltage"] = JsonSerializer.SerializeToElement(4.8),
                ["Sim.Current"] = JsonSerializer.SerializeToElement(0.25),
                ["Sim.Temperature"] = JsonSerializer.SerializeToElement(25.0),
                ["Sim.Enabled"] = JsonSerializer.SerializeToElement(true),
                ["Sim.SerialNumber"] = JsonSerializer.SerializeToElement("SN001"),
                ["Sim.Samples"] = JsonSerializer.SerializeToElement(new[] { 4.8, 4.9, 5.0 })
            };
            repository.Save(values);
        }
        return new(new SequenceWorkspaceRepository(workspace).Load(), values,
            new StationVariableRepository().Load());
    }

    public void Save(ProjectWorkspace workspace, IEnumerable<SequenceSubproject> subprojects,
        IEnumerable<SequenceVariable> stationVariables)
    {
        new SequenceWorkspaceRepository(workspace).Save(subprojects);
        new StationVariableRepository().Save(stationVariables);
    }

    public IReadOnlyList<SimulationScenario> LoadScenarios(ProjectWorkspace workspace) =>
        new SimulationScenarioRepository(workspace.Root).Load();

    public void SaveScenarios(ProjectWorkspace workspace, IReadOnlyList<SimulationScenario> scenarios) =>
        new SimulationScenarioRepository(workspace.Root).Save(scenarios);

    public IReadOnlyDictionary<string, JsonElement> LoadSimulation(ProjectWorkspace workspace) =>
        new SimulationRepository(workspace.Root).Load();

    public IReadOnlyList<SequenceChannelDefinition> LoadChannelDefinitions(ProjectWorkspace workspace) =>
        new SimulationRepository(workspace.Root).LoadDefinitions();

    public void SaveSimulation(ProjectWorkspace workspace, IReadOnlyDictionary<string, JsonElement> values,
        bool preserveDefinitions = false) =>
        new SimulationRepository(workspace.Root).Save(values, preserveDefinitions);
}
