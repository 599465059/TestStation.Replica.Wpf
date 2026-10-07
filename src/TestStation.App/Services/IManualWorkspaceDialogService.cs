namespace TestStation.App.Services;

public interface IManualWorkspaceDialogService
{
    Task<SequenceUnsavedChoice> AskUnsavedAsync(string name);
    Task<string?> RenameAsync(string name);
}
