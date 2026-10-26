using System.Text.Json;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.App.Services;

/// <summary>Execution boundary used by the editor; the transport may be OpenTAP or another host.</summary>
public interface ISequenceExecutionHost
{
    event Action<OpenTapHostEvent>? EventReceived;
    Task<OpenTapHostRun> RunAsync(SequenceDefinition sequence, IReadOnlyList<SequenceDefinition> sequences,
        IReadOnlyDictionary<string, object?> parameters, IReadOnlyDictionary<string, object?> simulation,
        IReadOnlyList<SequenceVariable>? globalDefinitions = null, IReadOnlyList<SequenceVariable>? stationDefinitions = null,
        IReadOnlyDictionary<string, JsonElement>? globalValues = null, IReadOnlyDictionary<string, JsonElement>? stationValues = null);
    Task PauseAsync();
    Task ResumeAsync();
    Task StopAsync();
}
