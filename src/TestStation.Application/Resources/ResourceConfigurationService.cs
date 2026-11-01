using System.Text.Json;
using TestStation.Application.Hardware;
using TestStation.Domain.Resources;

namespace TestStation.Application.Resources;

public interface IResourceConfigurationService
{
    TransportResource CreateDraft(string type);
    void Save(TransportResource draft);
    void Delete(string resourceId);
}

/// <summary>原始目录不交给表单直接修改；成功校验、写盘后才发布新目录。</summary>
public sealed class ResourceConfigurationService(IResourceCatalogService catalog, IResourceManager resources) : IResourceConfigurationService
{
    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    public TransportResource CreateDraft(string type)
    {
        return ResourceCatalogEditingService.CreateTransportDraft(type);
    }

    public void Save(TransportResource draft)
    {
        if (resources.IsLeased(draft.Id)) throw new ResourceLeaseException("资源正在使用，请先断开设备或关闭诊断");
        TransportSettingsAccessor.ValidateEndpoint(draft);
        var next = Clone(catalog.Catalog);
        var index = next.Transports.FindIndex(r => r.Id == draft.Id);
        if (index < 0) next.Transports.Add(Clone(draft)); else next.Transports[index] = Clone(draft);
        catalog.Save(next);
    }
    public void Delete(string resourceId)
    {
        if (resources.IsLeased(resourceId)) throw new ResourceLeaseException("资源正在使用，请先断开设备或关闭诊断");
        if (catalog.Catalog.Devices.Any(d => d.TransportId == resourceId)) throw new ResourceLeaseException("资源仍被设备绑定，请先解除绑定");
        var next = Clone(catalog.Catalog);
        next.Transports.RemoveAll(r => r.Id == resourceId);
        catalog.Save(next);
    }
}
