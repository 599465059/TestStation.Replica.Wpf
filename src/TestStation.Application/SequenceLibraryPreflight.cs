using System.Text.Json;
using TestStation.Domain;

namespace TestStation.Application;

/// <summary>Validates the reachable call graph without evaluating runtime expressions.</summary>
public static class SequenceLibraryPreflight
{
    public static SequencePreflightReport Validate(SequenceDefinition? root, IEnumerable<SequenceDefinition> sequences)
    {
        var issues = new List<SequencePreflightIssue>();
        var library = new Dictionary<string, SequenceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var sequence in sequences)
            if (!library.TryAdd(sequence.Id, sequence)) issues.Add(new("sequence.duplicate", $"序列 ID 重复: {sequence.Id}"));
        if (root is null) return SequencePreflightService.Validate(null);
        library[root.Id] = root;
        Visit(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new(issues);

        void Visit(SequenceDefinition sequence, HashSet<string> stack)
        {
            if (!stack.Add(sequence.Id)) { issues.Add(new("call.recursive", $"子序列形成递归调用: {string.Join(" → ", stack)} → {sequence.Id}")); return; }
            if (stack.Count > 64) { issues.Add(new("call.depth", "子序列调用层级超过 64")); stack.Remove(sequence.Id); return; }
            issues.AddRange(SequencePreflightService.Validate(sequence).Issues.Select(issue => issue with { Message = $"{sequence.Name}: {issue.Message}" }));
            if (sequence.Variables.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sequence.Variables.Count)
                issues.Add(new("variable.duplicate", $"{sequence.Name}: 变量名称重复（不区分大小写）"));
            foreach (var variable in sequence.Variables.Where(v => v.Scope == "local" && (v.Default is null || v.Default.Value.ValueKind == JsonValueKind.Null)))
                issues.Add(new("variable.required", $"{sequence.Name}/{variable.Name}: 局部变量必须有默认值"));
            foreach (var variable in sequence.Variables.Where(v => v.Default is not null))
            {
                try { SequenceParameterValue.Validate(variable.Default!.Value, variable.DataType); }
                catch (InvalidDataException error) { issues.Add(new("variable.default", $"{sequence.Name}/{variable.Name}: {error.Message}")); }
            }
            Inspect(sequence.SetupSteps, 0, 0); Inspect(sequence.Steps, 0, 0); Inspect(sequence.CleanupSteps, 0, 0);
            stack.Remove(sequence.Id);

            void Inspect(IEnumerable<SequenceStep> steps, int loops, int depth)
            {
                if (depth > 128) { issues.Add(new("step.depth", $"{sequence.Name}: 步骤嵌套超过 128 层")); return; }
                foreach (var step in steps)
                {
                    if ((step.Kind is "break" or "continue") && loops == 0)
                        Add("loop.flow", "BREAK/CONTINUE 只能放在当前序列的循环内");
                    foreach (var source in new[] { step.BeforeExpression, step.AfterExpression })
                    {
                        try { SequenceExpressionProgram.Parse(source); }
                        catch (InvalidDataException error) { Add("expression.program", error.Message); }
                    }
                    if ((step.Kind is "set_value" or "read_value") && step.Params.TryGetValue("signals", out var signalTable))
                    {
                        try
                        {
                            var rows = SequenceSignalOperation.Read(signalTable);
                            if (rows.Length == 0) Add("signals.empty", "信号表至少需要一行");
                            foreach (var row in rows) row.Validate(step.Kind == "read_value");
                        }
                        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException)
                        { Add("signals.invalid", error.Message); }
                    }
                    foreach (var expression in new[] { step.Precondition,
                        step.Kind is "if" or "while" or "statement" ? ReadText("expression") : "" })
                    {
                        if (string.IsNullOrWhiteSpace(expression)) continue;
                        try { SequenceExpression.ValidateSyntax(expression); }
                        catch (InvalidDataException error) { Add("expression.syntax", error.Message); }
                    }
                    if (step.Kind == "assert")
                    {
                        if (string.IsNullOrWhiteSpace(ReadText("channel_id"))) Add("assert.channel", "ASSERT 缺少通道");
                        var required = ReadText("comparator") is "IN_RANGE" or "OUTSIDE_RANGE" ? new[] { "minimum", "maximum" } : new[] { "expected" };
                        foreach (var key in required)
                            if (!step.Params.TryGetValue(key, out var expected) || expected.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                                Add("assert.expected", $"ASSERT 缺少 {key}");
                    }
                    if (step.Kind == "call_sequence")
                    {
                        var id = step.Params.GetValueOrDefault("sequence_id");
                        if (id.ValueKind != JsonValueKind.String || !library.TryGetValue(id.GetString() ?? "", out var target))
                            Add("call.missing", $"目标子序列不存在: {id}");
                        else
                        {
                            if (target.IsMain) { Add("call.main", "主序列不能作为被调用目标"); continue; }
                            var arguments = step.Params.GetValueOrDefault("arguments");
                            if (arguments.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object)) Add("call.arguments", "调用参数必须是对象");
                            else
                            {
                                var supplied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                                if (arguments.ValueKind == JsonValueKind.Object)
                                    foreach (var argument in arguments.EnumerateObject())
                                        if (!supplied.TryAdd(argument.Name, argument.Value)) Add("call.duplicate", $"调用参数重复: {argument.Name}");
                                var declared = target.Variables.Where(v => v.Scope == "parameter").ToArray();
                                foreach (var key in supplied.Keys)
                                    if (!declared.Any(v => v.Name.Equals(key, StringComparison.OrdinalIgnoreCase))) Add("call.unknown", $"子序列未声明参数: {key}");
                                foreach (var parameter in declared)
                                {
                                    if (!supplied.TryGetValue(parameter.Name, out var value))
                                    { if (parameter.Default is null || parameter.Default.Value.ValueKind == JsonValueKind.Null) Add("call.required", $"子序列参数未填写: {parameter.Name}"); }
                                    else if (!SequenceParameterValue.IsReference(value))
                                    {
                                        try { SequenceParameterValue.Validate(value, parameter.DataType); }
                                        catch (InvalidDataException error) { Add("call.type", $"{parameter.Name}: {error.Message}"); }
                                    }
                                    else
                                    {
                                        try { SequenceParameterValue.ValidateReference(value.GetString()!); }
                                        catch (InvalidDataException error) { Add("call.expression", $"{parameter.Name}: {error.Message}"); }
                                    }
                                }
                            }
                            Visit(target, stack);
                        }
                    }
                    var childLoops = loops + (step.Kind is "loop" or "while" ? 1 : 0);
                    Inspect(step.Children, childLoops, depth + 1); Inspect(step.ElseChildren, loops, depth + 1);
                    void Add(string code, string message) => issues.Add(new(code, $"{sequence.Name}/{step.Name}: {message}", step.Id));
                    string ReadText(string key) => step.Params.TryGetValue(key, out var value) ? value.ToString() : "";
                }
            }
        }
    }
}
