namespace TestStation.App.Services;

public sealed record ProjectSaveResult(bool Success, string Error = "");

public interface IProjectSaveParticipant
{
    string SaveSection { get; }
    ProjectSaveResult SaveProjectChanges();
}

/// <summary>Combines module save operations without referencing views or controls.</summary>
public sealed class ProjectSaveService
{
    private readonly List<IProjectSaveParticipant> _participants = [];
    public void Register(IProjectSaveParticipant participant)
    {
        if (!_participants.Contains(participant)) _participants.Add(participant);
    }
    public ProjectSaveResult Save()
    {
        foreach (var participant in _participants)
        {
            try
            {
                var result = participant.SaveProjectChanges();
                if (!result.Success) return new(false, $"{participant.SaveSection}：{result.Error}");
            }
            catch (Exception error) { return new(false, $"{participant.SaveSection}：{error.Message}"); }
        }
        return new(true);
    }
}
