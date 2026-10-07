using System.Text.Json;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.Infrastructure;

public sealed class ManualCanvasRepository(IManualCanvasEditingService editing) : IManualCanvasRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static string FilePath(string projectRoot, string documentId)
    {
        ManualWorkspaceEntry.ValidateId(documentId);
        return documentId == "main" ? Path.Combine(projectRoot, "layouts", "manual", "canvas.json")
            : Path.Combine(projectRoot, "layouts", "manual", "documents", documentId + ".json");
    }
    public ManualCanvasDefinition? Load(string projectRoot, string documentId = "main")
    {
        var path = FilePath(projectRoot, documentId);
        if (!File.Exists(path)) return null;
        var document = JsonSerializer.Deserialize<ManualCanvasDefinition>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Empty manual layout.");
        editing.Validate(document);
        return document;
    }
    public void Save(string projectRoot, ManualCanvasDefinition document, string documentId = "main")
    {
        editing.Validate(document);
        var path = FilePath(projectRoot, documentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options)); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
