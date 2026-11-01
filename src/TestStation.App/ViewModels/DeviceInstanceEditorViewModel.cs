using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TestStation.Application.Devices;
using TestStation.Application.Hardware;
using TestStation.App.Services;
using TestStation.Domain.Resources;

namespace TestStation.App.ViewModels;

public sealed partial class DeviceInstanceEditorViewModel : ObservableObject
{
    private readonly IResourceManager _resources;
    public IReadOnlyList<IManagedDeviceFamily> Families { get; }
    public IReadOnlyList<ManagedDeviceModel> Models => SelectedFamily?.Models ?? [];
    public IReadOnlyList<TransportResource> Resources => _resources.GetResources().Where(r => r.Enabled && SelectedModel?.TransportTypes.Contains(r.Type) == true).ToArray();
    public bool CanSelectFamily { get; }
    public string InstanceId { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private IManagedDeviceFamily? _selectedFamily;
    [ObservableProperty] private ManagedDeviceModel? _selectedModel;
    [ObservableProperty] private TransportResource? _selectedResource;
    [ObservableProperty] private string _error = "";
    public DeviceInstanceEditorViewModel(IDeviceManagementService devices, IResourceManager resources, IResourceEditorService editor, DeviceDefinition? existing)
    {
        _resources = resources; CanSelectFamily = existing is null; InstanceId = existing?.Id ?? "";
        Families = devices.Families.Where(f => f.Enabled || f.Id == existing?.FamilyId).ToArray();
        Name = existing?.Name ?? "";
        SelectedFamily = Families.FirstOrDefault(f => f.Id == existing?.FamilyId) ?? Families.FirstOrDefault();
        SelectedModel = Models.FirstOrDefault(m => m.Id == existing?.ModelId) ?? Models.FirstOrDefault();
        SelectedResource = Resources.FirstOrDefault(r => r.Id == existing?.TransportId);
    }
    partial void OnSelectedFamilyChanged(IManagedDeviceFamily? value) { OnPropertyChanged(nameof(Models)); SelectedModel = Models.FirstOrDefault(); }
    partial void OnSelectedModelChanged(ManagedDeviceModel? value)
    { OnPropertyChanged(nameof(Resources)); if (SelectedResource is not null && !Resources.Any(r => r.Id == SelectedResource.Id)) SelectedResource = null; }
    [RelayCommand] private void Unbind() => SelectedResource = null;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("设备名称不能为空");
        if (SelectedFamily?.Enabled != true || SelectedModel is null) throw new InvalidDataException("请选择已启用的家族和型号");
        if (SelectedResource is not null && !Resources.Any(r => r.Id == SelectedResource.Id)) throw new InvalidDataException("通信资源与型号不匹配");
    }
}
