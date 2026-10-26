using System.IO;
using System.Text.Json;
using TestStation.Application;
using TestStation.Domain;

namespace TestStation.Acceptance;

internal static class AcceptanceProject
{
    public static readonly (string Name, string Type, object Value)[] Values =
    [
        ("Number", "number", 1.25), ("Integer", "integer", 7L),
        ("Boolean", "boolean", true), ("Text", "text", "SN001"),
        ("Numbers", "number[]", new[] { 1.25, 2.5, 3.75 }),
        ("Integers", "integer[]", new[] { 1, 2, 3 }),
        ("Booleans", "boolean[]", new[] { true, false, true }),
        ("Texts", "text[]", new[] { "A", "B", "C" })
    ];
    public static string SamplePath(string root) => Path.Combine(root, "examples", "自动测试全功能验收");
    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
    public static SequenceVariable Variable(string name, string type, object? value, string scope = "local") =>
        new() { Name = name, DataType = type, Default = value is null ? null : Json(value), Scope = scope };
    public static object Empty(string type) => type.EndsWith("[]") ? Array.Empty<object>() :
        type == "text" ? "" : type == "boolean" ? false : 0;
    public static SequenceStep Step(string id, string kind, string name, object? parameters = null,
        List<SequenceStep>? children = null, List<SequenceStep>? otherwise = null) => new()
    {
        Id = id, Kind = kind, Name = name, Description = "自动测试验收：" + name,
        Params = parameters is null ? [] : Json(parameters).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()),
        Children = children ?? [], ElseChildren = otherwise ?? []
    };
    public static SequenceStep Statement(string id, string target, string expression) =>
        Step(id, "statement", id, new { target_variable = target, expression });
    public static SequenceStep Require(string id, string expression) => Step(id, "if", id, new { expression },
        [Step(id + "_pass", "comment", "检查通过")],
        [Step(id + "_fail", "assert", "检查失败", new { channel_id = "Sim.Number", comparator = "==", expected = -9999 })]);
    public static SequenceSignalOperation Write(string name, string type, string value, string mode = "constant") =>
        new() { ChannelId = "Sim." + name, DataType = type, Value = value, Mode = mode };
    public static SequenceSignalOperation Read(string name, string type, string comparator = "NONE", string expected = "",
        string mode = "constant", string arrayMode = "ALL", string minimum = "", string maximum = "", string tolerance = "0", string target = "") =>
        new() { ChannelId = "Sim." + name, DataType = type, Value = target, Comparator = comparator, Expected = expected,
            Mode = mode, ArrayMode = arrayMode, Minimum = minimum, Maximum = maximum, Tolerance = tolerance };
    public static SequenceStep Signals(string id, bool read, params SequenceSignalOperation[] rows) =>
        Step(id, read ? "read_value" : "set_value", id, new { signals = rows });

    public static string Create(string root)
    {
        var path = SamplePath(root);
        var workspace = File.Exists(Path.Combine(path, "project.json")) ? ProjectWorkspace.Open(path) :
            ProjectWorkspace.Create(Path.Combine(root, "examples"), "自动测试全功能验收");
        var functional = Functional();
        var auxiliary = Auxiliary();
        new SequenceWorkspaceRepository(workspace).Save([functional, auxiliary]);
        var simulation = Values.ToDictionary(v => "Sim." + v.Name, v => Json(v.Value), StringComparer.OrdinalIgnoreCase);
        simulation["Sim.Empty"] = Json(Array.Empty<double>());
        simulation["Legacy_Value"] = Json(12.5);
        new SimulationRepository(path).Save(simulation, channelDefinitions: Values
            .Select(value => new SequenceChannelDefinition("Sim." + value.Name, value.Type))
            .Concat([new("Sim.Empty", "number[]"), new("Legacy_Value", "number")]).ToArray());
        new SimulationScenarioRepository(path).Save([
            new("正常值", simulation), new("超限值", new Dictionary<string, JsonElement>(simulation) { ["Sim.Number"] = Json(99.0) })]);
        Directory.CreateDirectory(Path.Combine(path, "resources"));
        File.WriteAllText(Path.Combine(path, "resources", "station-variables.json"),
            JsonSerializer.Serialize(StationDefinitions(), new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static List<SequenceVariable> StationDefinitions() => Values.Select(v => Variable("AT_" + v.Name, v.Type, Empty(v.Type), "station"))
        .Concat([Variable("AT_Session", "integer", 0, "station")]).ToList();

    private static SequenceSubproject Functional()
    {
        var main = new SequenceDefinition { Id = "acceptance_main", Name = "01 全功能主序列（约65秒）", Role = "main",
            Description = "全部步骤类型、全部变量类型和作用域、信号表、比较、表达式、嵌套调用；13次5秒等待保证运行超过1分钟。" };
        main.Variables.AddRange(Values.Select(v => Variable("AT_" + v.Name, v.Type, Empty(v.Type))));
        main.Variables.AddRange(Values.Select(v => Variable("P_" + v.Name, v.Type, v.Value, "parameter")));
        main.Variables.AddRange([Variable("hook", "integer", 0), Variable("count", "integer", 0), Variable("whileCount", "integer", 0),
            Variable("descCount", "integer", 0), Variable("math", "number", 0), Variable("average", "number", 0)]);
        main.SetupSteps = [Step("initialize", "initialize", "初始化测量缓存"),
            Statement("reset_calls", "Globals.AT_Calls", "0"),
            Statement("increment_session", "Station.AT_Session", "Station.AT_Session + 1"),
            Step("setup_log", "log", "准备完成", new { level = "INFO", message = "进入65秒全功能验收，站计数=${Station.AT_Session}" })];
        main.Steps.Add(Step("comment", "comment", "覆盖备注、说明和步骤设置"));
        foreach (var value in Values) main.Steps.Add(Statement("parameter_" + value.Name, "Locals.AT_" + value.Name, "Locals.P_" + value.Name));
        main.Steps.Add(Signals("write_all_types", false, Values.Select(v => Write(v.Name, v.Type, "Locals.AT_" + v.Name, "expression")).ToArray()));
        foreach (var scope in new[] { "Locals", "Globals", "Station" })
            main.Steps.Add(Signals("read_" + scope, true, Values.Select(v => Read(v.Name, v.Type, "==",
                v.Type == "text" ? (string)v.Value : Json(v.Value).GetRawText(), arrayMode: v.Type.EndsWith("[]") ? "EXACT" : "ALL", target: scope + ".AT_" + v.Name)).ToArray()));
        main.Steps.Add(Require("scopes_and_parameters", string.Join(" && ", Values.Select(v =>
            $"Locals.AT_{v.Name} == Globals.AT_{v.Name} && Globals.AT_{v.Name} == Station.AT_{v.Name} && Locals.P_{v.Name} == Locals.AT_{v.Name}"))));
        var hooks = Step("hooks", "comment", "执行前后表达式及顺序");
        hooks.BeforeExpression = "Locals.hook = 1\nLocals.math = pow(2, 3) + sqrt(9)\nLocals.average = avg(Locals.AT_Numbers)";
        hooks.AfterExpression = "Locals.hook = Locals.hook + 2";
        main.Steps.Add(hooks);
        main.Steps.Add(Require("hook_order", "Locals.hook == 3 && Locals.math == 11 && Locals.average == 2.5"));
        var skipped = Statement("precondition_skip", "Locals.hook", "999"); skipped.Precondition = "false";
        skipped.BeforeExpression = "Locals.hook = 998"; skipped.AfterExpression = "Locals.hook = 997";
        main.Steps.Add(skipped);
        var disabled = Statement("disabled", "Locals.hook", "996"); disabled.Enabled = false; main.Steps.Add(disabled);
        var silent = Step("unrecorded", "log", "不记录结果的步骤", new { level = "DEBUG", message = "应实时显示，但不进入报表" });
        silent.RecordResult = false; main.Steps.Add(silent);
        main.Steps.Add(Require("skip_did_not_mutate", "Locals.hook == 3"));
        main.Steps.Add(Require("math_functions", "abs(-3)==3 && sqrt(9)==3 && sin(0)==0 && cos(0)==1 && tan(0)==0 && abs(log(exp(1))-1)<1e-12 && log10(100)==2 && exp(0)==1 && floor(1.9)==1 && ceil(1.1)==2 && pow(2,3)==8 && round(1.234,2)==1.23 && clamp(12,0,10)==10 && min([1,2,3])==1 && max(1,2,3)==3 && sum([1,2,3])==6 && avg([1,2,3])==2 && length('abc')==3 && length(Locals.AT_Numbers)==3 && Locals.AT_Numbers[1]==2.5 && Math.PI>3 && Math.E>2 && 1e-3*1000==1 && (7%3)==1 && !(false || false)"));
        main.Steps.Add(Signals("scalar_comparisons", true,
            Read("Number", "number", "==", "1.25"), Read("Number", "number", "!=", "2"),
            Read("Number", "number", ">", "1"), Read("Number", "number", ">=", "1.25"),
            Read("Number", "number", "<", "2"), Read("Number", "number", "<=", "1.25"),
            Read("Number", "number", "IN_RANGE", minimum: "1", maximum: "2"),
            Read("Number", "number", "OUTSIDE_RANGE", minimum: "2", maximum: "3"),
            Read("Number", "number", "ABS_TOL", "1.3", tolerance: "0.1"),
            Read("Number", "number", "REL_TOL", "1.3", tolerance: "5"),
            Read("Number", "number", "==", "Locals.P_Number", "expression"),
            Read("Number", "number", "IN_RANGE", mode: "expression", minimum: "Locals.P_Number-1", maximum: "Locals.P_Number+1"),
            Read("Number", "number", "ABS_TOL", "Locals.P_Number+0.05", "expression", tolerance: "0.1"),
            Read("Boolean", "boolean", "!=", "false"), Read("Text", "text", "CONTAINS", "SN"),
            Read("Text", "text", "REGEX", "^SN[0-9]{3}$"), Read("Text", "text", "NONE")));
        main.Steps.Add(Signals("array_comparisons", true,
            Read("Numbers", "number[]", "==", "[1.25,2.5,3.75]", arrayMode: "EXACT"),
            Read("Numbers", "number[]", ">", "0", arrayMode: "ALL"),
            Read("Numbers", "number[]", ">", "3", arrayMode: "ANY"),
            Read("Numbers", "number[]", "IN_RANGE", minimum: "1", maximum: "4"),
            Read("Numbers", "number[]", "IN_RANGE", arrayMode: "EXACT", minimum: "[1,2,3]", maximum: "[2,3,4]"),
            Read("Numbers", "number[]", "==", "Locals.P_Numbers", "expression", "EXACT"),
            Read("Numbers", "number[]", "ABS_TOL", "[1.2,2.4,3.7]", arrayMode: "EXACT", tolerance: "0.11"),
            Read("Numbers", "number[]", "REL_TOL", "[1.2,2.4,3.7]", arrayMode: "EXACT", tolerance: "5"),
            Read("Texts", "text[]", "REGEX", "^[A-C]$"), Read("Integers", "integer[]", "!=", "0"),
            Read("Booleans", "boolean[]", "==", "true", arrayMode: "ANY")));
        main.Steps.Add(Step("independent_assert", "assert", "独立ASSERT数组逐项相等", new { channel_id = "Sim.Numbers", comparator = "==", expected = new[] { 1.25, 2.5, 3.75 }, array_mode = "EXACT" }));
        main.Steps.Add(Step("assert_reference", "assert", "独立ASSERT引用变量", new { channel_id = "Sim.Number", comparator = "==", expected = "${Locals.P_Number}" }));
        main.Steps.Add(Step("true_branch", "if", "IF真分支", new { expression = "Locals.hook == 3" },
            [Statement("then_increment", "Locals.hook", "Locals.hook+1")], [Statement("wrong_else", "Locals.hook", "999")]));
        main.Steps.Add(Step("false_branch", "if", "IF假分支", new { expression = "false" },
            [Statement("wrong_then", "Locals.hook", "999")], [Statement("else_increment", "Locals.hook", "Locals.hook+1")]));
        main.Steps.Add(Step("for_continue_break", "loop", "FOR、CONTINUE与BREAK", new { variable = "i", from = 1, to = 5, step = 1, max_iterations = 10 },
        [Step("continue_if", "if", "第二轮CONTINUE", new { expression = "i == 2" }, [Step("continue", "continue", "CONTINUE")]),
            Statement("for_increment", "Locals.count", "Locals.count+1"),
            Step("break_if", "if", "第四轮BREAK", new { expression = "i == 4" }, [Step("break", "break", "BREAK")])]));
        main.Steps.Add(Step("for_desc", "loop", "FOR负步长", new { variable = "j", from = 3, to = 1, step = -1, max_iterations = 10 },
            [Statement("desc_increment", "Locals.descCount", "Locals.descCount+1")]));
        main.Steps.Add(Step("while", "while", "WHILE条件重新计算", new { expression = "Locals.whileCount < 3", max_iterations = 4 },
            [Statement("while_increment", "Locals.whileCount", "Locals.whileCount+1")]));
        main.Steps.Add(Require("loop_results", "Locals.count == 3 && Locals.descCount == 3 && Locals.whileCount == 3 && Locals.hook == 5"));
        main.Steps.Add(Step("call_sub", "call_sequence", "主序列调用子序列再调用子序列", new { sequence_id = "acceptance_sub", arguments = new Dictionary<string, object> { ["input"] = "${Locals.P_Number * 2}", ["items"] = "${Locals.P_Numbers}" } }));
        main.Steps.Add(Require("call_scope", "Locals.AT_Number == Locals.P_Number && Globals.AT_Calls == 2"));
        foreach (var level in new[] { "INFO", "WARN", "ERROR", "DEBUG" })
            main.Steps.Add(Step("log_" + level, "log", "日志级别" + level, new { level, message = "验收日志，不改变判定：${Locals.AT_Text}" }));
        var soak = Step("soak_loop", "loop", "13轮×5秒持续运行", new { variable = "tick", from = 1, to = 13, step = 1, max_iterations = 13 },
            [Step("soak_log", "log", "持续测试进度", new { level = "INFO", message = "持续测试 ${tick}/13，等待5秒" }),
                Step("soak_wait", "wait", "每轮等待5000毫秒", new { timeout_ms = "5000" })]);
        soak.TimeoutMs = 90000; main.Steps.Add(soak);
        main.CleanupSteps = [Step("cleanup_log", "log", "CLEANUP正常执行", new { level = "INFO", message = "正常主序列清理完成" }),
            Statement("cleanup_mark", "Globals.AT_Cleanup", "Globals.AT_Cleanup+1")];
        var sub = new SequenceDefinition { Id = "acceptance_sub", Name = "子序列A（可单独调试）", Role = "sub",
            Variables = [Variable("input", "number", 2.5, "parameter"), Variable("items", "number[]", new[] { 1.25, 2.5, 3.75 }, "parameter"), Variable("AT_Number", "number", -999)],
            Steps = [Require("sub_parameter", "Locals.input == 2.5 && sum(Locals.items) == 7.5 && Locals.AT_Number == -999"),
                Statement("sub_local", "Locals.AT_Number", "123"), Statement("sub_shared", "Globals.AT_Calls", "Globals.AT_Calls+1"),
                Step("nested_call", "call_sequence", "子序列A调用B", new { sequence_id = "acceptance_leaf", arguments = new { input = "${Locals.input}" } })],
            CleanupSteps = [Step("sub_cleanup", "comment", "子序列A清理")] };
        var leaf = new SequenceDefinition { Id = "acceptance_leaf", Name = "子序列B", Role = "sub",
            Variables = [Variable("input", "number", 2.5, "parameter")],
            Steps = [Require("leaf_parameter", "Locals.input == 2.5"), Statement("leaf_shared", "Globals.AT_Calls", "Globals.AT_Calls+1")],
            CleanupSteps = [Step("leaf_cleanup", "comment", "子序列B清理")] };
        var quick = new SequenceDefinition { Id = "acceptance_quick", Name = "02 同标签页共享值复核", Role = "sub",
            Steps = [Require("shared_retained", "Globals.AT_Number == 1.25 && Globals.AT_Cleanup >= 1 && Station.AT_Number == 1.25 && Station.AT_Session >= 1")] };
        return new SequenceSubproject { Id = "functional", Name = "01 正常功能与65秒运行", Sequences = [main, quick, sub, leaf],
            GlobalVariables = Values.Select(v => Variable("AT_" + v.Name, v.Type, Empty(v.Type), "global"))
                .Concat([Variable("AT_Calls", "integer", 0, "global"), Variable("AT_Cleanup", "integer", 0, "global")]).ToList() };
    }

    private static SequenceSubproject Auxiliary()
    {
        var isolation = new SequenceDefinition { Id = "tab_isolation", Name = "01 标签页隔离与站共享", Role = "sub",
            Steps = [Require("tab_global_isolated", "Globals.AT_Number == -20 && Station.AT_Number == 1.25 && Station.AT_Session >= 1"),
                Statement("tab_global_write", "Globals.AT_Number", "-21")] };
        var list = new List<SequenceDefinition> { isolation };
        foreach (var action in new[] { "CONTINUE", "GOTO_CLEANUP", "STOP" })
        {
            var fail = Signals("failure", true, Read("Number", "number", "IN_RANGE", minimum: "100", maximum: "200"));
            fail.OnFailure = action; fail.FailureMessage = "预期的范围判定失败：" + action;
            fail.BeforeExpression = "Locals.hook = 1"; fail.AfterExpression = "Locals.hook = Locals.hook+1";
            list.Add(new SequenceDefinition { Id = "failure_" + action.ToLowerInvariant(), Name = "预期FAIL · " + action, Role = "sub",
                Variables = [Variable("hook", "integer", 0)], Steps = [fail, Statement("after_failure", "Locals.hook", "Locals.hook+10")],
                CleanupSteps = [Step("failure_cleanup", "log", "异常路径CLEANUP", new { level = "INFO", message = "失败后的hook=${Locals.hook}" }),
                    Statement("failure_cleanup_mark", "Globals.AT_Cleanup", "Globals.AT_Cleanup+1")] });
        }
        var timeout = Step("timeout_wait", "wait", "步骤超时", new { timeout_ms = "500" }); timeout.TimeoutMs = 100; timeout.OnFailure = "GOTO_CLEANUP";
        list.Add(new SequenceDefinition { Id = "timeout", Name = "预期FAIL · 步骤超时", Role = "sub", Steps = [timeout], CleanupSteps = [Step("timeout_cleanup", "comment", "超时后清理")] });
        list.Add(new SequenceDefinition { Id = "cancel", Name = "取消与暂停（需手动停止）", Role = "sub",
            Steps = [Step("cancel_wait", "wait", "等待30秒，供停止测试", new { timeout_ms = "30000" })] });
        var badArrays = new[] {
            ("array_mismatch", Read("Numbers", "number[]", "==", "[1.25,2.5,99]", arrayMode: "EXACT")),
            ("array_length", Read("Numbers", "number[]", "==", "[1.25,2.5]", arrayMode: "EXACT")),
            ("empty_array", Read("Empty", "number[]", ">", "0")),
            ("any_array_fail", Read("Numbers", "number[]", ">", "100", arrayMode: "ANY")),
            ("boolean_mismatch", Read("Boolean", "boolean", "==", "false")),
            ("text_mismatch", Read("Text", "text", "REGEX", "^INVALID$")) };
        foreach (var (id, row) in badArrays) list.Add(new SequenceDefinition { Id = id, Name = "预期FAIL · " + id, Role = "sub", Steps = [Signals(id, true, row)] });
        var divide = Step("bad_formula", "comment", "除零错误"); divide.BeforeExpression = "Locals.result = 1 / 0";
        list.Add(new SequenceDefinition { Id = "divide_zero", Name = "预期ERROR · 除零", Role = "sub", Variables = [Variable("result", "number", 0)], Steps = [divide] });
        list.Add(new SequenceDefinition { Id = "required_parameter", Name = "必填运行参数", Role = "sub", Variables = [Variable("required", "integer", null, "parameter")],
            Steps = [Require("required_received", "Locals.required == 42")] });
        return new SequenceSubproject { Id = "negative", Name = "02 隔离、异常与控制测试", Sequences = list,
            GlobalVariables = [Variable("AT_Number", "number", -20, "global"), Variable("AT_Cleanup", "integer", 0, "global")] };
    }
}
