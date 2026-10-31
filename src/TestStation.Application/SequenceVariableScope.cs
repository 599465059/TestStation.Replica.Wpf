using System.Text.Json;
using System.Text.RegularExpressions;
using TestStation.Domain;

namespace TestStation.Application;

/// <summary>A typed variable frame shared by the preview executor and the OpenTAP adapter.</summary>
public sealed class SequenceVariableScope
{
    public Dictionary<string, object?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SequenceVariableScope(IEnumerable<SequenceVariable> definitions,
        IReadOnlyDictionary<string, object?>? arguments = null, bool shared = false)
    {
        var variables = definitions.ToArray();
        foreach (var key in arguments?.Keys ?? [])
            if (!variables.Any(v => (shared || v.Scope == "parameter") && v.Name.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"未声明的运行参数: {key}");
        foreach (var variable in variables)
        {
            if (!Regex.IsMatch(variable.Name, @"^[_\p{L}][_\p{L}\p{N}]*$") || Types.ContainsKey(variable.Name))
                throw new InvalidDataException($"变量名称无效或重复: {variable.Name}");
            if (!shared && variable.Scope is not ("local" or "parameter"))
                throw new InvalidDataException($"变量作用域无效: {variable.Scope}");
            var initial = (shared || variable.Scope == "parameter") && arguments?.TryGetValue(variable.Name, out var supplied) == true
                ? supplied : variable.Default is { } value ? SequenceValueOperations.Unwrap(value) : null;
            if (shared && initial is null)
                initial = variable.DataType.EndsWith("[]", StringComparison.Ordinal) ? Array.Empty<object?>() :
                    variable.DataType == "text" ? "" : variable.DataType == "boolean" ? false : 0;
            if (initial is null) throw new InvalidDataException($"变量缺少值: {variable.Name}");
            Types.Add(variable.Name, variable.DataType);
            Assign(variable.Name, initial);
        }
    }

    public void Assign(string name, object? value)
    {
        if (!Types.TryGetValue(name, out var type)) throw new InvalidDataException($"变量未声明: {name}");
        Values[name] = SequenceValueOperations.ConvertTo(value, type);
    }
}

/// <summary>Shared definitions and current values for one sequence subproject and station.</summary>
public sealed class SequenceExecutionEnvironment
{
    public IReadOnlyList<SequenceVariable> GlobalVariables { get; init; } = [];
    public IReadOnlyList<SequenceVariable> StationVariables { get; init; } = [];
    public Dictionary<string, object?> Globals { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, object?> Station { get; } = new(StringComparer.OrdinalIgnoreCase);
}
