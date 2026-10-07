namespace TestStation.App.Services;

public enum SequenceUnsavedChoice { Save, Discard, Cancel }

public interface ISequenceDialogService
{
    Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name);
    Task<bool> ConfirmDeleteSequenceAsync(string name);
    Task<string?> RenameSequenceAsync(string name);
    Task<bool> ConfirmStopExecutionAsync();
}

// Noninteractive callers must explicitly supply their decision policy.
internal sealed class CancelSequenceDialogs : ISequenceDialogService
{
    public Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name) => Task.FromResult(SequenceUnsavedChoice.Cancel);
    public Task<bool> ConfirmDeleteSequenceAsync(string name) => Task.FromResult(false);
    public Task<string?> RenameSequenceAsync(string name) => Task.FromResult<string?>(null);
    public Task<bool> ConfirmStopExecutionAsync() => Task.FromResult(false);
}
