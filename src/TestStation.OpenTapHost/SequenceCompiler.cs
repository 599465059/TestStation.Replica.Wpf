using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestStation.OpenTapHost;

/// <summary>Preserves sequence boundaries and control-flow branches for the OpenTAP adapter.</summary>
public sealed class SequenceCompiler
{
    private static readonly HashSet<string> SupportedKinds =
    ["initialize", "set_value", "read_value", "wait", "assert", "if", "loop", "while",
        "break", "continue", "log", "statement", "call_sequence", "comment"];

    public OpenTapPlan Compile(JsonElement sequence, IReadOnlyList<JsonElement>? sequenceLibrary = null)
    {
        var library = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sequenceLibrary ?? [])
        {
            var id = RequiredString(entry, "id");
            if (!library.TryAdd(id, entry)) throw new InvalidDataException($"序列 ID 重复: {id}");
        }
        library[RequiredString(sequence, "id")] = sequence;
        return CompileSequence(sequence, library, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private static OpenTapPlan CompileSequence(JsonElement sequence, IReadOnlyDictionary<string, JsonElement> library,
        HashSet<string> callStack)
    {
        var id = RequiredString(sequence, "id");
        if (callStack.Count >= 64) throw new InvalidDataException("子序列调用层级超过 64");
        if (!callStack.Add(id)) throw new InvalidDataException($"序列调用形成递归: {id}");
        try
        {
            var stepIds = new HashSet<string>(StringComparer.Ordinal);
            var sections = new List<OpenTapPlanSection>();
            foreach (var (key, name) in new[] { ("setup_steps", "Setup"), ("steps", "Main"), ("cleanup_steps", "Cleanup") })
            {
                sections.Add(new OpenTapPlanSection
                {
                    Name = name,
                    Steps = CompileSteps(Array(sequence, key), library, callStack, stepIds, 0, 0)
                });
            }
            var variables = Array(sequence, "variables").Select(value => new OpenTapPlanVariable
            {
                Name = RequiredString(value, "name"),
                DataType = String(value, "data_type", "number"),
                Scope = String(value, "scope", "local"),
                Default = value.TryGetProperty("default", out var initial) ? initial.Clone() : null
            }).ToArray();
            if (variables.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variables.Length)
                throw new InvalidDataException($"{id}: 变量名称重复");
            return new OpenTapPlan
            {
                SequenceId = id, SequenceName = RequiredString(sequence, "name"),
                Version = Number(sequence, "version", 1), Variables = variables, Sections = sections
            };
        }
        finally { callStack.Remove(id); }
    }

    private static IReadOnlyList<OpenTapPlanStep> CompileSteps(IEnumerable<JsonElement> values,
        IReadOnlyDictionary<string, JsonElement> library, HashSet<string> callStack, HashSet<string> stepIds,
        int loopDepth, int depth)
    {
        if (depth > 128) throw new InvalidDataException("步骤嵌套超过 128 层");
        var steps = new List<OpenTapPlanStep>();
        foreach (var value in values)
        {
            var id = RequiredString(value, "id");
            if (!stepIds.Add(id)) throw new InvalidDataException($"步骤 ID 重复: {id}");
            var kind = RequiredString(value, "kind").ToLowerInvariant();
            if (!SupportedKinds.Contains(kind)) throw new InvalidDataException($"不支持的步骤类型: {kind}");
            if ((kind is "break" or "continue") && loopDepth == 0)
                throw new InvalidDataException($"{id}: {kind.ToUpperInvariant()} 只能放在当前序列的循环内");
            var children = Array(value, "children").ToArray();
            var elseChildren = Array(value, "else_children").ToArray();
            if (children.Length > 0 && kind is not ("if" or "loop" or "while"))
                throw new InvalidDataException($"{id}: 此步骤不能包含子步骤");
            if (elseChildren.Length > 0 && kind != "if")
                throw new InvalidDataException($"{id}: 只有 IF 可以包含 ELSE 分支");
            var parameters = Object(value, "params");
            TestStation.Application.SequenceExpressionProgram.Parse(String(value, "before_expression"));
            TestStation.Application.SequenceExpressionProgram.Parse(String(value, "after_expression"));
            if ((kind is "set_value" or "read_value") && parameters.TryGetValue("signals", out var table))
            {
                var rows = TestStation.Application.SequenceSignalOperation.Read(table);
                if (rows.Length == 0) throw new InvalidDataException("信号表至少需要一行");
                foreach (var row in rows) row.Validate(kind == "read_value");
            }
            var onFailure = String(value, "on_failure", "STOP").ToUpperInvariant();
            var onComplete = String(value, "on_complete", "CONTINUE").ToUpperInvariant();
            // Older files stored the ASSERT failure action in params.
            if (!value.TryGetProperty("on_failure", out _) && kind == "assert" && parameters.TryGetValue("failure_action", out var oldAction))
                onFailure = oldAction.GetString()?.ToUpperInvariant() == "STOP" ? "STOP" : "CONTINUE";
            if (onFailure is not ("STOP" or "CONTINUE" or "GOTO_CLEANUP"))
                throw new InvalidDataException($"{id}: 失败动作无效");
            if (onComplete is not ("STOP" or "CONTINUE" or "GOTO_CLEANUP"))
                throw new InvalidDataException($"{id}: 完成动作无效");
            var timeout = Number(value, "timeout_ms", 0);
            if (timeout < 0) throw new InvalidDataException($"{id}: 超时不能小于 0");
            OpenTapPlan? calledSequence = null;
            if (kind == "call_sequence")
            {
                if (!parameters.TryGetValue("sequence_id", out var target) || target.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"{id}: 子序列调用缺少 sequence_id");
                var targetId = target.GetString() ?? "";
                if (!library.TryGetValue(targetId, out var definition)) throw new InvalidDataException($"目标序列不存在: {targetId}");
                if (String(definition, "role", "sub") == "main") throw new InvalidDataException($"不能调用主序列: {targetId}");
                calledSequence = CompileSequence(definition, library, callStack);
            }
            var childLoopDepth = loopDepth + (kind is "loop" or "while" ? 1 : 0);
            steps.Add(new OpenTapPlanStep
            {
                Id = id, Kind = kind, Name = String(value, "name", kind),
                Enabled = Bool(value, "enabled", true), TimeoutMs = timeout, OnFailure = onFailure, OnComplete = onComplete,
                Description = String(value, "description"), FailureMessage = String(value, "failure_message"),
                Precondition = String(value, "precondition"), RecordResult = Bool(value, "record_result", true),
                BeforeExpression = String(value, "before_expression"), AfterExpression = String(value, "after_expression"),
                Parameters = parameters, CalledSequence = calledSequence,
                Children = CompileSteps(children, library, callStack, stepIds, childLoopDepth, depth + 1),
                ElseChildren = CompileSteps(elseChildren, library, callStack, stepIds, childLoopDepth, depth + 1)
            });
        }
        return steps;
    }

    private static IEnumerable<JsonElement> Array(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var result)) return [];
        if (result.ValueKind != JsonValueKind.Array) throw new InvalidDataException($"{key} 必须是 JSON 数组");
        return result.EnumerateArray();
    }

    private static Dictionary<string, JsonElement> Object(JsonElement value, string key)
    {
        if (!value.TryGetProperty(key, out var result)) return new(StringComparer.Ordinal);
        if (result.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{key} 必须是 JSON 对象");
        return result.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }

    private static string RequiredString(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("序列和步骤必须是 JSON 对象");
        var result = String(value, key);
        return string.IsNullOrWhiteSpace(result) ? throw new InvalidDataException($"{key} 不能为空") : result;
    }

    private static string String(JsonElement value, string key, string fallback = "")
    {
        if (!value.TryGetProperty(key, out var result)) return fallback;
        if (result.ValueKind != JsonValueKind.String) throw new InvalidDataException($"{key} 必须是字符串");
        return result.GetString() ?? fallback;
    }

    private static int Number(JsonElement value, string key, int fallback)
    {
        if (!value.TryGetProperty(key, out var result)) return fallback;
        if (result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out var number))
            throw new InvalidDataException($"{key} 必须是整数");
        return number;
    }

    private static bool Bool(JsonElement value, string key, bool fallback)
    {
        if (!value.TryGetProperty(key, out var result)) return fallback;
        if (result.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException($"{key} 必须是布尔值");
        return result.GetBoolean();
    }
}

public sealed record OpenTapPlan
{
    [JsonPropertyName("sequence_id")] public required string SequenceId { get; init; }
    [JsonPropertyName("sequence_name")] public required string SequenceName { get; init; }
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("variables")] public IReadOnlyList<OpenTapPlanVariable> Variables { get; init; } = [];
    [JsonPropertyName("sections")] public required IReadOnlyList<OpenTapPlanSection> Sections { get; init; }
}

public sealed record OpenTapPlanVariable
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("data_type")] public string DataType { get; init; } = "number";
    [JsonPropertyName("scope")] public string Scope { get; init; } = "local";
    [JsonPropertyName("default")] public JsonElement? Default { get; init; }
}

public sealed record OpenTapPlanSection
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("steps")] public required IReadOnlyList<OpenTapPlanStep> Steps { get; init; }
}

public sealed record OpenTapPlanStep
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("timeout_ms")] public int TimeoutMs { get; init; }
    [JsonPropertyName("on_failure")] public string OnFailure { get; init; } = "STOP";
    [JsonPropertyName("on_complete")] public string OnComplete { get; init; } = "CONTINUE";
    [JsonPropertyName("precondition")] public string Precondition { get; init; } = "";
    [JsonPropertyName("before_expression")] public string BeforeExpression { get; init; } = "";
    [JsonPropertyName("after_expression")] public string AfterExpression { get; init; } = "";
    [JsonPropertyName("failure_message")] public string FailureMessage { get; init; } = "";
    [JsonPropertyName("record_result")] public bool RecordResult { get; init; } = true;
    [JsonPropertyName("params")] public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = new Dictionary<string, JsonElement>();
    [JsonPropertyName("children")] public IReadOnlyList<OpenTapPlanStep> Children { get; init; } = [];
    [JsonPropertyName("else_children")] public IReadOnlyList<OpenTapPlanStep> ElseChildren { get; init; } = [];
    [JsonPropertyName("called_sequence")] public OpenTapPlan? CalledSequence { get; init; }
}
