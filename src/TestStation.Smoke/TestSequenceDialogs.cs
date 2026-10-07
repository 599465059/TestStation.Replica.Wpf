using TestStation.App.Services;
namespace TestStation.Smoke;
internal sealed class TestSequenceDialogs : ISequenceDialogService
{
    public SequenceUnsavedChoice Choice { get; set; } = SequenceUnsavedChoice.Save;
    public bool Delete { get; set; } = true;
    public bool Stop { get; set; } = true;
    public string? Name { get; set; }
    public int UnsavedCalls { get; private set; }
    public int DeleteCalls { get; private set; }
    public int StopCalls { get; private set; }
    public Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name) { UnsavedCalls++; return Task.FromResult(Choice); }
    public Task<bool> ConfirmDeleteSequenceAsync(string name) { DeleteCalls++; return Task.FromResult(Delete); }
    public Task<string?> RenameSequenceAsync(string name) => Task.FromResult(Name);
    public Task<bool> ConfirmStopExecutionAsync() { StopCalls++; return Task.FromResult(Stop); }
}
