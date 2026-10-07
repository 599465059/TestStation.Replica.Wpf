namespace TestStation.Application;

public sealed record ManualWorkspaceEntry(string Id, string Name, bool IsOpen = true)
{
    public static void ValidateId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 80 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException("手动测试工区标识无效");
    }
}

public interface IManualWorkspaceRepository
{
    IReadOnlyList<ManualWorkspaceEntry> Load(string projectRoot);
    void Save(string projectRoot, IReadOnlyCollection<ManualWorkspaceEntry> entries);
}
