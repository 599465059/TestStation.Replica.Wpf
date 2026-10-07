using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestStation.Domain;

public sealed class SequenceDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("setup_steps")] public List<SequenceStep> SetupSteps { get; set; } = [];
    [JsonPropertyName("steps")] public List<SequenceStep> Steps { get; set; } = [];
    [JsonPropertyName("cleanup_steps")] public List<SequenceStep> CleanupSteps { get; set; } = [];
    [JsonPropertyName("variables")] public List<SequenceVariable> Variables { get; set; } = [];
}

public sealed class SequenceVariable
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("data_type")] public string DataType { get; set; } = "number";
    [JsonPropertyName("default")] public JsonElement? Default { get; set; }
    [JsonPropertyName("scope")] public string Scope { get; set; } = "local";
}

public sealed class SequenceStep
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "comment";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("params")] public Dictionary<string, JsonElement> Params { get; set; } = [];
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("children")] public List<SequenceStep> Children { get; set; } = [];
    [JsonPropertyName("else_children")] public List<SequenceStep> ElseChildren { get; set; } = [];
}
