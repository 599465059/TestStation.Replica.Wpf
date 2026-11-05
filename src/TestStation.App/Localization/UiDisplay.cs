using System.Text.Json;
using TestStation.Domain;

namespace TestStation.App.Localization;

/// <summary>Display-only translations. Model identifiers and user text remain separate.</summary>
public static class UiDisplay
{
    public static string StepType(string kind) => kind.ToUpperInvariant() switch
    {
        "IF" => "IF", "LOOP" or "FOR" => "FOR",
        "WHILE" => "WHILE", "BREAK" => "BREAK", "CONTINUE" => "CONTINUE",
        "LOG" => "LOG", "STATEMENT" => "STATEMENT",
        "INITIALIZE" => UiText.Initialize,
        "SET_VALUE" or "SET VALUE" => UiText.SetValue,
        "READ_VALUE" or "READ VALUE" or "GET VALUE" => UiText.GetValue,
        "WAIT" => UiText.Wait, "ASSERT" => UiText.Assert,
        "CALL_SEQUENCE" or "CALL SEQUENCE" or "SEQUENCE CALL" => UiText.SequenceCall,
        "COMMENT" => UiText.Comment,
        _ => kind
    };

    public static string StepName(SequenceStep step)
    {
        var oldDefault = step.Kind switch
        {
            "loop" => "FOR", "read_value" => "GET VALUE", "call_sequence" => "SEQUENCE CALL",
            _ => step.Kind.ToUpperInvariant().Replace('_', ' ')
        };
        return step.Name == oldDefault ? StepType(step.Kind) : step.Name;
    }

    public static string Section(string section) => section.ToUpperInvariant() switch
    {
        "SETUP" => UiText.Setup, "MAIN" => UiText.Main, "CLEANUP" => UiText.Cleanup, _ => section
    };

    public static string State(string state) => state.ToUpperInvariant() switch
    {
        "IDLE" => UiText.Idle, "RUNNING" => UiText.Running, "PAUSED" => UiText.Paused,
        "PASSED" or "COMPLETED" => UiText.Passed, "FAILED" => UiText.Failed, "STOPPED" => UiText.Stopped,
        "SKIPPED" => UiText.Skipped, "PENDING" => UiText.Pending, _ => state
    };

    /// <summary>
    /// Row status text. Judgment steps report 通过/失败; ordinary steps report
    /// 完成/异常. Steps that have not run show a dash so an idle table stays quiet.
    /// </summary>
    public static string StepState(string state, bool hasJudgment) => state.ToUpperInvariant() switch
    {
        "PASSED" or "COMPLETED" => hasJudgment ? UiText.Passed : "完成",
        "FAILED" => hasJudgment ? UiText.Failed : "异常",
        "RUNNING" => UiText.Running,
        "SKIPPED" => UiText.Skipped,
        "STOPPED" => UiText.Stopped,
        "" or "PENDING" => "—",
        _ => state
    };

    /// <summary>Semantic status category; the view maps it to theme brushes.</summary>
    public static string StepStateKind(string state) => state.ToUpperInvariant() switch
    {
        "PASSED" or "COMPLETED" => "success",
        "FAILED" => "error",
        "RUNNING" => "running",
        _ => "idle"
    };

    public static string LogLevel(string level) => level.ToUpperInvariant() switch
    {
        "INFO" => UiText.Info, "WARN" or "WARNING" => UiText.Warning,
        "ERROR" => UiText.Error, "DEBUG" => UiText.Debug, _ => level
    };

    public static string Option(string value) => value switch
    {
        "number" => UiText.Number, "integer" => UiText.Integer, "boolean" => UiText.Boolean,
        "text" => UiText.Text, "local" => "局部", "global" => "全局（当前标签页）", "station" => "站变量（整个软件）", "parameter" => "运行参数（局部）",
        "STOP" => UiText.StopOnFailure, "CONTINUE" => UiText.ContinueOnFailure,
        "number[]" => "数字数组", "integer[]" => "整数数组", "boolean[]" => "布尔数组", "text[]" => "文本数组",
        "constant" => "常量", "expression" => "变量 / 公式", "NONE" => "不比较", "ALL" => "全部元素满足", "ANY" => "任一元素满足", "EXACT" => "同长度逐项满足",
        "OUTSIDE_RANGE" => "范围外", "ABS_TOL" => "绝对容差", "REL_TOL" => "相对容差 (%)", "CONTAINS" => "包含文本", "REGEX" => "正则匹配",
        "LOG" => UiText.LogOnly, "IN_RANGE" => UiText.InRange,
        "read_request" => UiText.ReadRequest, "read_response" => UiText.ReadResponse,
        "write_request" => UiText.WriteRequest, "write_response" => UiText.WriteResponse, "event" => UiText.Event,
        "header" => UiText.FrameHeader, "id" => UiText.Identifier, "length" => UiText.Length,
        "data" => UiText.Data, "checksum" => UiText.Checksum, "tail" => UiText.FrameTail, "skip" => UiText.Reserved,
        "big" => UiText.BigEndian, "little" => UiText.LittleEndian,
        "none" => UiText.NoChecksum, "sum8" => UiText.Sum8, "xor8" => UiText.Xor8,
        "crc8" => UiText.Crc8, "crc16" => UiText.Crc16, "modbus_crc16" or "crc16_modbus" => UiText.ModbusCrc16,
        "serial" => UiText.Serial, "can" => UiText.Can, "tcp" => UiText.Tcp, "udp" => UiText.Udp,
        "bool" => UiText.BoolType, "uint8" => UiText.UInt8, "int8" => UiText.Int8,
        "uint16" => UiText.UInt16, "int16" => UiText.Int16, "uint32" => UiText.UInt32, "int32" => UiText.Int32,
        "uint64" => UiText.UInt64, "int64" => UiText.Int64, "float32" => UiText.Float32, "float64" => UiText.Float64,
        "ascii" => UiText.Ascii, "hex" => UiText.Hex,
        "signed / bit" => UiText.SignedBit, "unsigned / bit" => UiText.UnsignedBit,
        _ => value
    };

    public static string Parameter(string key) => key switch
    {
        "channel_id" => UiText.Channel, "value" => UiText.Value, "timeout_ms" => UiText.TimeoutMs,
        "comparator" => UiText.Comparator, "expected" => UiText.Expected, "minimum" => UiText.Minimum,
        "maximum" => UiText.Maximum, "failure_action" => UiText.OnFailure, "expression" => UiText.Condition,
        "variable" => UiText.Variable, "from" => UiText.From, "to" => UiText.To, "step" => UiText.Increment,
        "sequence_id" => UiText.Sequence, "arguments" => UiText.Arguments,
        "max_iterations" => "最大循环次数", "level" => "日志级别", "message" => "日志消息",
        "target_variable" => UiText.Variable,
        _ => key
    };

    public static string ParameterValue(string key, JsonElement value) =>
        (key is "failure_action" or "comparator") && value.ValueKind == JsonValueKind.String
            ? Option(value.GetString() ?? "") : value.ToString();

    // Translate only known engine message prefixes, never arbitrary variable values,
    // descriptions or imported user names. Persisted reports retain the raw events.
    public static string LogMessage(string message)
    {
        foreach (var section in new[] { "Setup", "Main", "Cleanup" })
        {
            var prefix = $"开始: {section} / ";
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                return UiText.Format(nameof(UiText.StepStarted), Section(section), DefaultStepName(message[prefix.Length..]));
            if (message.StartsWith(section + " / ", StringComparison.Ordinal))
                return Section(section) + message[section.Length..];
        }
        foreach (var (prefix, resource) in new[] {
            ("序列结束: ", nameof(UiText.SequenceEnded)),
            ("ASSERT PASS: ", nameof(UiText.AssertPassed)),
            ("ASSERT FAIL: ", nameof(UiText.AssertFailed)),
            ("Cleanup 执行失败: ", nameof(UiText.CleanupFailed)) })
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                return UiText.Format(resource, resource == nameof(UiText.SequenceEnded)
                    ? State(message[prefix.Length..]) : message[prefix.Length..]);
        if (message == "Cleanup 步骤失败") return UiText.CleanupStepFailed;
        foreach (var kind in new[] { "SET VALUE", "GET VALUE", "WAIT", "SEQUENCE CALL", "ASSERT" })
            if (message.StartsWith(kind + " ", StringComparison.Ordinal))
                return StepType(kind) + message[kind.Length..];
        return message;
    }

    private static string DefaultStepName(string name) => name is
        "INITIALIZE" or "SET VALUE" or "GET VALUE" or "WAIT" or "ASSERT" or "SEQUENCE CALL" or "COMMENT"
            ? StepType(name) : name;
}
