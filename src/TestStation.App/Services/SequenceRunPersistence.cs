using System.IO;
using System.Text.Json;
using TestStation.Application;

namespace TestStation.App.Services;

public interface ISequenceRunPersistence
{
    Task SaveAsync(string projectRoot, SequenceRunResult result, IReadOnlyList<OpenTapHostEvent> events);
}

public sealed class SequenceRunPersistence : ISequenceRunPersistence
{
    public Task SaveAsync(string projectRoot, SequenceRunResult result, IReadOnlyList<OpenTapHostEvent> events)
    {
        return Task.Run(() =>
        {
            var repository = new RunResultRepository(projectRoot);
            repository.Save(result);
            var path = Path.Combine(projectRoot, "reports", result.Id + ".opentap.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        });
    }
}
