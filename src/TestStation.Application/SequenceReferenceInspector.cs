using System.Text.Json;
using System.Text.RegularExpressions;
using TestStation.Domain;

namespace TestStation.Application;

public static class SequenceReferenceInspector
{
    public static IReadOnlyList<string> FindVariableUses(IEnumerable<SequenceDefinition> sequences, string name, string scope)
    {
        var references = new List<string>();
        var qualified = scope switch { "global" => $"Globals.{name}", "station" => $"Station.{name}", _ => name };
        foreach (var sequence in sequences)
        foreach (var step in SequenceEditor.Walk(sequence))
        {
            var location = $"{sequence.Name}/{step.Name}";
            if (ContainsExpression(step.Precondition, name, qualified) ||
                ContainsExpression(step.BeforeExpression, name, qualified) ||
                ContainsExpression(step.AfterExpression, name, qualified) ||
                ContainsParameters(step.Params, name, qualified))
                references.Add(location);
        }
        return references.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool ContainsParameters(IReadOnlyDictionary<string, JsonElement> parameters, string name, string qualified)
    {
        foreach (var pair in parameters)
        {
            if (pair.Key is "target_variable" or "value" && pair.Value.ValueKind == JsonValueKind.String &&
                IsName(pair.Value.GetString() ?? "", name, qualified)) return true;
            if (pair.Value.ValueKind == JsonValueKind.String)
            {
                var text = pair.Value.GetString() ?? "";
                if (pair.Key is "expression" or "expected" or "minimum" or "maximum" or "tolerance" or "arguments")
                {
                    if (ContainsExpression(text, name, qualified)) return true;
                }
                if (text.Contains("${" + name + "}", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("${" + qualified + "}", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (pair.Value.ValueKind == JsonValueKind.Object && ContainsParameters(
                pair.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value), name, qualified)) return true;
            if (pair.Value.ValueKind == JsonValueKind.Array && pair.Value.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && ContainsExpression(item.GetString() ?? "", name, qualified))) return true;
        }
        return false;
    }

    private static bool IsName(string value, string name, string qualified) =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase) || value.Equals(qualified, StringComparison.OrdinalIgnoreCase) ||
        value.Equals("${" + name + "}", StringComparison.OrdinalIgnoreCase) || value.Equals("${" + qualified + "}", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsExpression(string expression, string name, string qualified)
    {
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try
        {
            if (SequenceExpression.GetReferences(expression).Any(reference =>
                reference.Equals(name, StringComparison.OrdinalIgnoreCase) || reference.Equals(qualified, StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch (InvalidDataException) { }
        return Regex.IsMatch(expression, $@"\$\{{(?:{Regex.Escape(name)}|{Regex.Escape(qualified)})\}}", RegexOptions.IgnoreCase);
    }
}
