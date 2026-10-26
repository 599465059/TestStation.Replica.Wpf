namespace TestStation.Application;

/// <summary>Stable step metadata shared by validation and future plugin registration.</summary>
public sealed record SequenceStepDescriptor(
    string Kind,
    string DisplayName,
    bool HasJudgment = false,
    bool SupportsChildren = false,
    bool SupportsElse = false);

public interface ISequenceStepCatalog
{
    IReadOnlyList<SequenceStepDescriptor> GetAll();
    SequenceStepDescriptor? Find(string kind);
}

/// <summary>Plugin boundary for steps. Providers are discovered by the host at composition time.</summary>
public interface ISequenceStepProvider
{
    IReadOnlyList<SequenceStepDescriptor> GetSteps();
}

public sealed class BuiltInSequenceStepCatalog : ISequenceStepCatalog
{
    private static readonly IReadOnlyList<SequenceStepDescriptor> Steps =
    [
        new("initialize", "初始化"), new("set_value", "设置值"), new("read_value", "读取值", true),
        new("wait", "等待"), new("assert", "断言", true), new("if", "IF", false, true, true),
        new("loop", "FOR", false, true), new("while", "WHILE", false, true),
        new("break", "BREAK"), new("continue", "CONTINUE"), new("call_sequence", "调用序列"),
        new("log", "日志"), new("statement", "赋值"), new("comment", "注释")
    ];
    public IReadOnlyList<SequenceStepDescriptor> GetAll() => Steps;
    public SequenceStepDescriptor? Find(string kind) => Steps.FirstOrDefault(step =>
        step.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
}
