using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestStation.Domain;

public sealed class SequenceDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("role")] public string Role { get; set; } = "sub";
    [JsonIgnore] public bool IsMain => Role == "main";
    [JsonPropertyName("setup_steps")] public List<SequenceStep> SetupSteps { get; set; } = [];
    [JsonPropertyName("steps")] public List<SequenceStep> Steps { get; set; } = [];
    [JsonPropertyName("cleanup_steps")] public List<SequenceStep> CleanupSteps { get; set; } = [];
    [JsonPropertyName("variables")] public List<SequenceVariable> Variables { get; set; } = [];
}

public sealed class SequenceSubproject : System.ComponentModel.INotifyPropertyChanged
{
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    private string _name = "序列子工程";
    [JsonPropertyName("name")] public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    [JsonPropertyName("sequences")] public List<SequenceDefinition> Sequences { get; set; } = [];
    [JsonPropertyName("global_variables")] public List<SequenceVariable> GlobalVariables { get; set; } = [];
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
    /// <summary>Maximum time allowed for this step. Zero means use the engine default.</summary>
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; set; }
    /// <summary>Controlled action when this step fails: STOP, CONTINUE, or GOTO_CLEANUP.</summary>
    [JsonPropertyName("on_failure")] public string OnFailure { get; set; } = "STOP";
    [JsonPropertyName("on_complete")] public string OnComplete { get; set; } = "CONTINUE";
    [JsonPropertyName("failure_message")] public string FailureMessage { get; set; } = "";
    [JsonPropertyName("precondition")] public string Precondition { get; set; } = "";
    [JsonPropertyName("before_expression")] public string BeforeExpression { get; set; } = "";
    [JsonPropertyName("after_expression")] public string AfterExpression { get; set; } = "";
    [JsonPropertyName("record_result")] public bool RecordResult { get; set; } = true;
    [JsonPropertyName("children")] public List<SequenceStep> Children { get; set; } = [];
    [JsonPropertyName("else_children")] public List<SequenceStep> ElseChildren { get; set; } = [];
}
