using System.Text.Json;
using TestStation.Application.Resources;
using TestStation.Domain.Resources;

namespace TestStation.Infrastructure.Resources;

public sealed class ResourceCatalogRepository : IResourceCatalogRepository
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
    private static string FilePath(string root) => Path.Combine(root, "resources", "resources.json");

    public ResourceCatalogSnapshot Load(string root)
    {
        var path = FilePath(root);
        if (!File.Exists(path)) return new(new ResourceCatalog(), []);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var rootElement = document.RootElement;
            if (rootElement.TryGetProperty("format", out var format) && format.GetString() != "test-station-resources")
                throw new InvalidDataException("不是 Test Station 资源文件");
            if (rootElement.TryGetProperty("version", out var version) && version.GetInt32() > 1)
                throw new InvalidDataException("资源文件版本高于当前软件支持的版本");
            var payload = rootElement.TryGetProperty("resources", out var wrapped) ? wrapped : rootElement;
            var catalog = payload.Deserialize<ResourceCatalog>(Options) ?? new();
            ResourceCatalogValidator.Validate(catalog);
            return new(catalog, []);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            return new(new ResourceCatalog(), [$"{Path.GetFileName(path)}: {error.Message}"]);
        }
    }

    public void Save(string root, ResourceCatalog catalog)
    {
        ResourceCatalogValidator.Validate(catalog);
        var folder = Path.Combine(root, "resources");
        Directory.CreateDirectory(folder);
        var path = FilePath(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var payload = new { format = "test-station-resources", version = 1, resources = catalog };
            File.WriteAllText(temporary, JsonSerializer.Serialize(payload, Options));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
