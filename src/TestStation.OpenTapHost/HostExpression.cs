using TestStation.Application;

namespace TestStation.OpenTapHost;

// Share the parser source without referencing the .NET 10 application from this process.
public static class HostExpression
{
    public static bool Evaluate(string expression, IReadOnlyDictionary<string, object?> values) =>
        SequenceExpression.Evaluate(expression, values);

    public static object? Value(string expression, IReadOnlyDictionary<string, object?> values) =>
        SequenceExpression.EvaluateValue(expression, values);
}
