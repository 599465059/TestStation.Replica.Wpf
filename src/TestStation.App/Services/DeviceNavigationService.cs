namespace TestStation.App.Services;

public sealed record DeviceNavigationRequest(string FamilyId, string DeviceId);
public sealed class DeviceNavigationService
{
    public event EventHandler<DeviceNavigationRequest>? Requested;
    public void Open(string familyId, string deviceId) => Requested?.Invoke(this, new(familyId, deviceId));
}
