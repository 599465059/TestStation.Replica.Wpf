namespace TestStation.Application;

/// <summary>A model's supported channels; independent of its UI and transport.</summary>
public sealed record DeviceModelDefinition(string Id, string Name, IReadOnlyList<string> Channels)
{
    public bool Supports(string channelId) => Channels.Contains(channelId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Shares the selected device model between manual panels and channel consumers.</summary>
public interface IDeviceModelCatalog
{
    event EventHandler? Changed;
    IReadOnlyList<DeviceModelDefinition> GetModels(string familyId);
    DeviceModelDefinition GetSelected(string familyId);
    void Select(string familyId, string modelId);
}
