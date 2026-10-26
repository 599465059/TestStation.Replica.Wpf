using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TestStation.App;
using TestStation.App.Services;
using TestStation.App.ViewModels;
using TestStation.Application;
using TestStation.Domain;
using ThemeMode = TestStation.App.Services.ThemeMode;

namespace TestStation.Acceptance;

internal sealed class AcceptanceSuite(string sample, string output)
{
    public List<object> Checks { get; } = [];
    private MainViewModel _model = null!;
    private SequenceWorkspace _view = null!;
    private Window _window = null!;
    private readonly AcceptanceDialogs _dialogs = new();
    private readonly ThemeService _theme = new();
    private int _eventProgress;
    private void Check(bool condition, string name)
    {
        Checks.Add(new { name, passed = condition });
        File.AppendAllText(Path.Combine(output, "checks.log"), $"{DateTimeOffset.Now:O} {(condition ? "PASS" : "FAIL")} {name}{Environment.NewLine}");
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
        if (!condition) throw new InvalidOperationException(name + " | " + _model?.Status);
    }
    public void RecordFailure(Exception error)
    {
        Checks.Add(new { name = error.Message, passed = false });
        File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
        if (_window is { IsVisible: true }) Capture("failure");
    }

    public async Task RunAsync(bool followOnly = false)
    {
        PrepareStationDefinitions();
        VerifyExpressionValidation();
        var editPath = Path.Combine(output, "editing-workspace");
        CopyProject(sample, editPath);
        _model = NewModel(editPath);
        _theme.Apply(ThemeMode.Dark, AccentPalette.Blue);
        _view = new SequenceWorkspace { DataContext = _model };
        _view.Configure(new SequenceInteractionService(new SequenceDropTargetService()));
        _window = new Window { Title = "自动测试全功能验收 · 实际WPF编辑器与OpenTAP", Content = _view,
            Width = 1680, Height = 980, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)System.Windows.Application.Current.Resources["WindowBrush"] };
        _window.Show();
        await Task.Delay(200);
        if (followOnly)
        {
            await VerifyExecutionFollowAsync();
            _window.Close();
            return;
        }
        await VerifyEditorAsync(editPath);
        _model = NewModel(sample); _view.DataContext = _model;
        await Task.Delay(100);
        await VerifyLongRunAsync();
        await VerifySharedAndDebugAsync();
        await VerifyFailureRunsAsync();
        await VerifyPauseStopAsync();
        await VerifyHostGuardsAsync();
        await VerifyExecutionFollowAsync();
        _model = NewModel(sample); _view.DataContext = _model;
        await SelectAsync("functional", "acceptance_main");
        _model.SelectedRow = _model.Rows.First(row => row.Step?.Id == "scalar_comparisons");
        await Task.Delay(100); Capture("completed-editor");
        _window.Close();
    }

    private MainViewModel NewModel(string path)
    {
        var model = new MainViewModel(new AutoTestDisplayService(), new SequenceEditingService(), sequenceDialogs: _dialogs);
        model.OpenProject(path); return model;
    }

    private async Task VerifyExecutionFollowAsync()
    {
        var workspace = ProjectWorkspace.Create(output, "execution-follow");
        var leaf = new SequenceDefinition { Id = "follow_leaf", Name = "第二层子序列", Role = "sub",
            Steps = [AcceptanceProject.Step("same_wait", "wait", "第二层当前步骤", new { timeout_ms = "700" })] };
        var child = new SequenceDefinition { Id = "follow_child", Name = "第一层子序列", Role = "sub", Steps = [
            AcceptanceProject.Step("same_wait", "wait", "第一层当前步骤", new { timeout_ms = "700" }),
            AcceptanceProject.Step("call_leaf", "call_sequence", "调用第二层", new { sequence_id = leaf.Id }),
            AcceptanceProject.Step("child_return", "wait", "返回第一层后的步骤", new { timeout_ms = "700" })] };
        var root = new SequenceDefinition { Id = "follow_main", Name = "运行跟随主序列", Role = "main", Steps = [
            AcceptanceProject.Step("call_child", "call_sequence", "首次调用第一层", new { sequence_id = child.Id }),
            AcceptanceProject.Step("same_wait", "wait", "返回主序列后的步骤", new { timeout_ms = "700" }),
            AcceptanceProject.Step("call_again", "call_sequence", "重复调用第一层", new { sequence_id = child.Id }),
            AcceptanceProject.Step("root_return", "wait", "再次返回主序列", new { timeout_ms = "700" })] };
        var tab = new SequenceSubproject { Id = "follow_tab", Name = "运行跟随验证", Sequences = [root, child, leaf] };
        new SequenceWorkspaceRepository(workspace).Save([tab]);
        _model = NewModel(workspace.Root); _view.DataContext = _model;
        var before = JsonSerializer.Serialize(_model.SequenceSubprojects);
        var transitions = new List<object>();
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CurrentExecutionRow) && _model.CurrentExecutionRow is { } row)
                transitions.Add(new { sequence_id = _model.SelectedSequence?.Id, step_id = row.Step?.Id, row.IsCurrent });
        };
        var run = _model.RunSequenceCommand.ExecuteAsync(null);
        bool At(string seq, string step) => _model.SelectedSequence?.Id == seq &&
            _model.CurrentExecutionRow is { IsCurrent: true, Status: "RUNNING" } row && row.Step?.Id == step;
        await Until(() => At(child.Id, "same_wait"), TimeSpan.FromSeconds(15));
        Check(_model.SelectedRow == _model.CurrentExecutionRow && _model.Rows.Count(r => r.IsCurrent) == 1,
            "进入子序列自动切换页面并唯一高亮对应步骤");
        Check(!_model.CanEditSequence && !await _model.SelectSequenceAsync(_model.Sequences.Single(s => s.Id == root.Id)),
            "自动跟随时保持编辑和手动切换锁定");
        await _model.PauseResumeCommand.ExecuteAsync(null);
        await Until(() => _model.ExecutionState == "PAUSED", TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        Check(At(child.Id, "same_wait"), "暂停期间保留子序列页面与步骤高亮");
        Capture("follow-child-paused");
        await _model.PauseResumeCommand.ExecuteAsync(null);
        await Until(() => At(leaf.Id, "same_wait"), TimeSpan.FromSeconds(10));
        Check(_model.Rows.Count(r => r.IsCurrent) == 1, "子序列调用子序列自动跟随第二层");
        Capture("follow-leaf");
        await Until(() => At(child.Id, "child_return"), TimeSpan.FromSeconds(10));
        Check(_model.Rows.Single(r => r.RowType == "step" && r.Step?.Id == "same_wait").Status == "PASSED",
            "子序列返回调用者并保留调用者已完成步骤状态");
        await Until(() => At(root.Id, "same_wait"), TimeSpan.FromSeconds(10));
        Check(_model.Rows.Single(r => r.RowType == "step" && r.Step?.Id == "call_child").Status == "PASSED",
            "返回主序列自动切回并高亮后续步骤");
        await Until(() => At(child.Id, "same_wait"), TimeSpan.FromSeconds(10));
        Check(_model.CurrentExecutionRow!.Status == "RUNNING", "重复调用子序列重新标记当前步骤，序列间同名步骤不串状态");
        await run.WaitAsync(TimeSpan.FromSeconds(15));
        Check(_model.SelectedSequence?.Id == root.Id && _model.ExecutionState == "PASSED" &&
            _model.CurrentExecutionRow is null && _model.Rows.All(r => !r.IsCurrent), "主序列结束返回运行入口并清除高亮");
        Check(JsonSerializer.Serialize(_model.SequenceSubprojects) == before, "运行自动导航不修改序列、参数或变量定义");
        var reports = new RunResultRepository(workspace.Root).LoadSummaries();
        Check(reports.Count == 1 && new RunResultRepository(workspace.Root).LoadResult(reports[0].Id)!.SequenceId == root.Id,
            "显示子序列不会改变主序列报告入口");
        await _model.SelectSequenceAsync(_model.Sequences.Single(s => s.Id == child.Id));
        var debug = _model.RunSequenceCommand.ExecuteAsync(null);
        await Until(() => At(leaf.Id, "same_wait"), TimeSpan.FromSeconds(10));
        await _model.StopSequenceCommand.ExecuteAsync(null); await debug;
        Check(_model.SelectedSequence?.Id == child.Id && _model.ExecutionState == "STOPPED" && _model.CurrentExecutionRow is null,
            "在第二层停止后返回原调试入口并清除高亮");
        Check(new RunResultRepository(workspace.Root).LoadSummaries().Count == 1, "调试子序列即使显示更深子序列仍不生成报告");
        File.WriteAllText(Path.Combine(output, "execution-follow-transitions.json"), JsonSerializer.Serialize(transitions,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PrepareStationDefinitions()
    {
        var repository = new StationVariableRepository();
        var definitions = repository.Load();
        File.WriteAllText(Path.Combine(output, "station-variables-before.json"), JsonSerializer.Serialize(definitions, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var variable in AcceptanceProject.StationDefinitions())
        {
            var current = definitions.FirstOrDefault(v => v.Name.Equals(variable.Name, StringComparison.OrdinalIgnoreCase));
            if (current is null) definitions.Add(variable);
            else if (current.DataType != variable.DataType || current.Scope != "station")
                throw new InvalidDataException($"验收站变量冲突: {variable.Name}，保留原定义并停止");
        }
        repository.Save(definitions);
        Check(AcceptanceProject.StationDefinitions().All(v => definitions.Any(d => d.Name == v.Name)), "准备专用AT_站变量，保留并备份已有定义");
    }

    private void VerifyExpressionValidation()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        { ["Locals.n"] = 4, ["Globals.a"] = new[] { 1.0, 2.0, 3.0 }, ["Station.flag"] = true, ["pi"] = 7 };
        Check(SequenceExpression.Evaluate("sqrt(Locals.n) + avg(Globals.a) == 4 && Station.flag && pi == 7", values), "数学函数、跨作用域引用和保留常量不遮蔽变量");
        Check(SequenceExpression.Evaluate("Globals.a == [1,2,3] && Globals.a != [1,2,4] && Globals.a[0] == 1", values), "数组相等、不相等和零基下标");
        Reject(() => SequenceExpression.ValidateSyntax("pow(1)"), "函数参数数量校验");
        Reject(() => SequenceExpression.ValidateSyntax("1 + )"), "公式语法错误校验");
        Reject(() => SequenceExpression.ValidateReferences("Locals.missing + 1", values.Keys), "未声明变量校验");
        Reject(() => SequenceExpression.EvaluateValue("1/0", values), "公式除零校验");
        Reject(() => SequenceExpression.EvaluateValue("Globals.a[10]", values), "数组越界校验");
        Reject(() => SequenceExpression.EvaluateValue("sqrt(-1)", values), "非有限公式结果校验");
        Reject(() => SequenceValueOperations.ConvertTo(new[] { 1.5 }, "integer[]"), "数组元素类型校验");
        Reject(() => SequenceParameterValue.Parse("[1,]", "number[]"), "数组格式校验");
        Reject(() => SequenceExpressionProgram.Parse("Locals.n == 1"), "前后表达式要求赋值语句");
        Reject(() => AcceptanceProject.Read("Number", "number", "IN_RANGE", minimum: "3", maximum: "1").Validate(true), "上下限倒置校验");
        Reject(() => AcceptanceProject.Read("Number", "number", "REGEX", "x").Validate(true), "不适用的数据比较类型校验");
        Reject(() => AcceptanceProject.Read("Text", "text", "REGEX", "[").Validate(true), "无效正则校验");
        Reject(() => AcceptanceProject.Read("Number", "number", "ABS_TOL", "1", tolerance: "-1").Validate(true), "负容差校验");
        Reject(() => AcceptanceProject.Read("Numbers", "number[]", "==", "1", arrayMode: "EXACT").Validate(true), "数组逐项比较要求数组期望值");
        foreach (var type in SequenceValueOperations.DataTypes)
        {
            var v = AcceptanceProject.Values.Single(v => v.Type == type);
            var parsed = SequenceParameterValue.Validate(AcceptanceProject.Json(v.Value), type);
            Check(parsed.ValueKind != JsonValueKind.Null, "参数类型解析 " + type);
        }
    }

    private async Task VerifyEditorAsync(string editPath)
    {
        Check(_model.SequenceSubprojects.Count == 2 && _model.SelectedSequence!.IsMain, "工程包含多个标签页且默认选中主序列");
        Check(_model.CallSequenceChoices.All(s => !s.IsMain), "调用目标列表排除主序列");
        var main = _model.SelectedSequence!;
        _model.SelectedRow = _model.Rows.First(r => r.Step?.Id == "write_all_types");
        Check(_model.SignalValueRows.Count == 8, "设置值属性加载8种类型的信号表");
        var validation = Find<Button>(_view).First(b => Equals(b.Content, "校验公式与变量"));
        Click(validation); await Task.Delay(50);
        Check(_model.ExpressionValidationMessage.StartsWith("校验通过"), $"实际点击WPF表达式校验按钮（启用={validation.IsEnabled}，绑定={validation.Command is not null}，反馈={_model.ExpressionValidationMessage}）");
        var row = _model.SignalValueRows[0]; var original = row.Value;
        row.Value = "Locals.does_not_exist"; Click(validation); await Task.Delay(50);
        Check(_model.ExpressionValidationMessage.Contains("未声明"), "校验按钮拒绝信号表中的未声明变量");
        row.Value = original;
        Check(_model.ExpressionValidationMessage == "", "编辑后清除过期的校验结果");
        _model.StepBeforeExpression = "Locals.hook = 1 +"; Click(validation);
        Check(_model.ExpressionValidationMessage.Contains("失败"), "校验按钮拒绝错误的步骤前公式");
        _model.StepBeforeExpression = "";
        _model.AddSignalRowCommand.Execute(null);
        Check(_model.SignalValueRows.Count == 9, "信号表添加行");
        _model.SelectedSignalValueRow!.Target = "Sim.Numbers";
        Check(_model.SelectedSignalValueRow.DataType == "number[]", "选择数组通道自动带出数据类型");
        _model.RemoveSignalRowCommand.Execute(null);
        Check(_model.SignalValueRows.Count == 8, "信号表删除行");
        row.Value = "Locals.P_Number * 1"; _model.StepBeforeExpression = "Locals.hook = 1"; _model.StepAfterExpression = "Locals.hook = 3";
        _model.ApplyStepCommand.Execute(null); _model.SaveSequenceCommand.Execute(null);
        var loaded = new SequenceWorkspaceRepository(ProjectWorkspace.Open(editPath)).Load()[0].Sequences.Single(s => s.Id == main.Id);
        Check(loaded.Steps.Single(s => s.Id == "write_all_types").BeforeExpression == "Locals.hook = 1" &&
            SequenceSignalOperation.Read(loaded.Steps.Single(s => s.Id == "write_all_types").Params["signals"])[0].Value == "Locals.P_Number * 1", "信号表与前后表达式保存并重新加载");
        var previousName = _model.SelectedRow!.Step!.Name;
        _model.StepName = "验收修改名称"; _model.ApplyStepCommand.Execute(null);
        _model.UndoSequenceCommand.Execute(null);
        Check(_model.SelectedRow!.Step!.Name == previousName, "步骤属性撤销");
        _model.RedoSequenceCommand.Execute(null);
        Check(_model.SelectedRow!.Step!.Name == "验收修改名称", "步骤属性重做");
        _model.SaveSequenceCommand.Execute(null);
        foreach (var scope in new[] { "local", "global", "station", "parameter" })
        {
            _model.NewVariableCommand.Execute(scope); _model.VariableName = "AT_Edit_" + scope;
            _model.VariableType = "number[]"; _model.VariableDefault = "[1,2,3]";
            _model.ValidateVariableCommand.Execute(null);
            Check(_model.Status.Contains("校验通过"), "变量校验按钮 " + scope);
            _model.ApplyVariableCommand.Execute(null);
            Check(_model.Variables.Any(v => v.Name == "AT_Edit_" + scope && v.Scope == scope && v.Default!.Value.GetArrayLength() == 3), "新建和应用数组变量 " + scope);
            _dialogs.Name = "AT_Renamed_" + scope;
            await _model.RenameVariableCommand.ExecuteAsync(null);
            Check(_model.SelectedVariable?.Name == _dialogs.Name, "右键共用命令重命名变量 " + scope);
            await _model.DeleteVariableCommand.ExecuteAsync(null);
            Check(_model.Variables.All(v => v.Name != _dialogs.Name), "右键共用命令删除变量 " + scope);
        }
        _model.SaveSequenceCommand.Execute(null);
        await _model.SelectSequenceAsync(_model.Sequences.Single(s => s.Id == "acceptance_sub"));
        _model.CopySubsequenceCommand.Execute(null);
        var sourceIds = _model.Sequences.Select(s => s.Id).ToHashSet();
        _model.NewSubprojectCommand.Execute(null);
        Check(_model.SelectedSequence!.IsMain && _model.Sequences.Count == 1, "新建标签页默认创建一个主序列");
        _model.PasteSubsequenceCommand.Execute(null);
        Check(_model.Sequences.Count == 3 && _model.Sequences.Where(s => !s.IsMain).All(s => !sourceIds.Contains(s.Id)), "跨标签页复制子序列及依赖并生成新ID");
        var copied = _model.SelectedSequence!;
        Check(SequenceLibraryPreflight.Validate(copied, _model.Sequences).IsValid, "复制后依赖调用重新关联且预检通过");
        Check(!_model.NewMainSequenceCommand.CanExecute(null) && _model.Sequences.Count(s => s.IsMain) == 1,
            "每个标签页固定只有一个主序列，新建入口已禁用");
        _dialogs.Name = "验收重命名标签"; await _model.RenameSubprojectCommand.ExecuteAsync(null);
        Check(_model.SelectedSubproject!.Name == _dialogs.Name, "标签页重命名");
        await _model.DeleteSubprojectCommand.ExecuteAsync(null);
        Check(_model.SequenceSubprojects.Count == 2, "标签页删除");
        await SelectAsync("functional", "acceptance_main");
        _model.AddStep("COMMENT", new SequenceInsertTarget("Main", null, false, 0));
        var added = _model.SelectedRow!.Step!;
        Check(main.Steps[0].Id == added.Id, "步骤库插入步骤");
        _model.MoveStep(added.Id, new SequenceInsertTarget("Setup", null, false, 0));
        Check(main.SetupSteps[0].Id == added.Id, "步骤拖放共用事务移动到Setup");
        _model.SelectedRow = _model.Rows.First(r => r.Step?.Id == added.Id);
        _model.DeleteStepCommand.Execute(null);
        Check(!SequenceEditor.Walk(main).Any(s => s.Id == added.Id), "步骤删除");
        _model.SelectedSimulationScenario = _model.SimulationScenarios.Single(s => s.Name == "超限值"); _model.LoadSimulationScenarioCommand.Execute(null);
        Check(_model.SimulationValues.Single(v => v.Channel == "Sim.Number").Value.GetDouble() == 99, "载入命名模拟场景");
        _model.SimulationScenarioName = "验收临时场景"; _model.SaveSimulationScenarioCommand.Execute(null);
        Check(_model.SimulationScenarios.Any(s => s.Name == "验收临时场景"), "保存命名模拟场景");
        _model.DeleteSimulationScenarioCommand.Execute(null);
        Check(_model.SimulationScenarios.All(s => s.Name != "验收临时场景"), "删除命名模拟场景");
        foreach (var mode in new[] { ThemeMode.Light, ThemeMode.Dark })
        {
            _theme.Apply(mode, AccentPalette.Blue);
            _model.SelectedRow = _model.Rows.First(r => r.Step?.Id == "read_Locals");
            await Task.Delay(120); Capture("signal-table-" + mode);
            Check(Find<DataGrid>(_view).Any(grid => ReferenceEquals(grid.ItemsSource, _model.SignalValueRows) && grid.IsVisible), "信号表实际渲染 " + mode);
        }
        _model.SaveSequenceCommand.Execute(null);
    }

    private async Task VerifyLongRunAsync()
    {
        await SelectAsync("functional", "acceptance_main");
        _model.ValidateSequenceCommand.Execute(null);
        Check(_model.Status == "预检通过", "65秒主序列运行前预检");
        var clock = Stopwatch.StartNew();
        Click((Button)_view.FindName("RunButton"));
        var run = _model.RunSequenceCommand.ExecutionTask ?? throw new InvalidOperationException("运行按钮未启动任务");
        await Until(() => _model.IsExecutionBusy, TimeSpan.FromSeconds(10));
        Check(!_model.CanEditSequence && !_model.NewMainSequenceCommand.CanExecute(null), "运行期间锁定编辑和新建入口");
        var tab = _model.SelectedSubproject;
        _model.SelectedSubproject = _model.SequenceSubprojects.Single(p => p.Id == "negative");
        Check(_model.SelectedSubproject == tab, "运行期间禁止切换标签页");
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, Task.Delay(5000));
            if (clock.Elapsed > TimeSpan.FromMinutes(3)) { await _model.StopSequenceCommand.ExecuteAsync(null); throw new TimeoutException("正常主序列超过3分钟"); }
            var progress = _model.RunLogs.LastOrDefault(l => l.Message.Contains("持续测试"))?.Message ?? _model.Status;
            Console.WriteLine($"RUN {clock.Elapsed.TotalSeconds:F1}s {_model.ExecutionState}: {progress}");
            if (++_eventProgress == 2) Capture("running");
        }
        await run;
        Check(_model.ExecutionState == "PASSED", "实际WPF运行按钮驱动OpenTAP完成全功能主序列");
        Check(clock.Elapsed.TotalSeconds >= 65, "正常序列实际运行超过1分钟（" + clock.Elapsed.TotalSeconds.ToString("F2") + "秒）");
        var repository = new RunResultRepository(sample);
        var summary = repository.LoadSummaries().First(s => repository.LoadResult(s.Id)?.SequenceId == "acceptance_main");
        var result = repository.LoadResult(summary.Id)!;
        File.WriteAllText(Path.Combine(output, "main-run-id.txt"), result.Id);
        Check(result.Status == "completed" && result.Steps.All(s => s.Status is "passed" or "skipped"), "主序列全部记录通过或按设置跳过");
        Check(result.Steps.Count(s => s.StepId == "soak_wait" && s.Status == "passed") == 13, "13次真实5秒WAIT均完成");
        Check(result.Steps.Where(s => s.StepId == "soak_wait").Sum(s => s.DurationMs) >= 64500, "等待步骤累计执行时间符合65秒设定");
        Check(result.Steps.Where(s => s.StepId == "soak_wait").Select(s => s.StepRunId).Distinct().Count() == 13, "循环每次执行具有独立执行ID");
        Check(result.Steps.Any(s => s.SequenceId == "acceptance_sub") && result.Steps.Any(s => s.SequenceId == "acceptance_leaf"), "嵌套子序列结果汇入主报表");
        Check(result.Steps.Any(s => s.StepId == "cleanup_mark") && !result.Steps.Any(s => s.StepId == "unrecorded"), "CLEANUP记录且RecordResult=false不进入报表");
        Check(result.Steps.Single(s => s.StepId == "precondition_skip").Status == "skipped" && result.Steps.Single(s => s.StepId == "disabled").Status == "skipped", "前置条件与禁用步骤跳过");
        Check(File.Exists(summary.HtmlPath) && File.Exists(Path.Combine(sample, "reports", result.Id + ".opentap.json")), "生成HTML、结果JSON及OpenTAP原始事件");
        using var events = JsonDocument.Parse(File.ReadAllText(Path.Combine(sample, "reports", result.Id + ".opentap.json")));
        Check(events.RootElement.EnumerateArray().Any(e => e.GetProperty("type").GetString() == "result"), "实际产生OpenTAP结果表事件");
        Check(_model.ResultTables.Count >= 3 && _model.RuntimeValues.Any(v => v.Value.StartsWith('[')), "界面展示结果表和数组运行值");
        Check(_dialogs.ParameterCount >= 8, "运行参数窗口模型处理全部8种类型");
        Capture("main-passed");
    }

    private async Task VerifySharedAndDebugAsync()
    {
        await RunExpectedAsync("functional", "acceptance_quick", "PASSED");
        await RunExpectedAsync("negative", "tab_isolation", "PASSED");
        await RunExpectedAsync("functional", "acceptance_quick", "PASSED");
        var before = new RunResultRepository(sample).LoadSummaries().Count;
        await RunExpectedAsync("functional", "acceptance_sub", "PASSED");
        Check(new RunResultRepository(sample).LoadSummaries().Count == before && _model.Status.Contains("不生成报表"), "单独调试子序列不写报表和运行历史");
    }

    private async Task VerifyFailureRunsAsync()
    {
        foreach (var action in new[] { "continue", "goto_cleanup", "stop" })
        {
            var result = await RunExpectedAsync("negative", "failure_" + action, "FAILED");
            Check(result!.Steps.Any(s => s.StepId == "failure_cleanup_mark"), action + "失败后运行CLEANUP");
            Check(result.Steps.Any(s => s.StepId == "after_failure") == (action == "continue"), action + "后续主体控制正确");
            Check(result.Steps.Single(s => s.StepId == "failure").Message.Contains("预期的范围判定失败"), action + "自定义失败消息保留");
            var hook = result.Values["Locals.hook"];
            Check(SequenceValueOperations.Number(hook) == (action == "continue" ? 12 : 2), action + "判定失败仍执行后表达式");
        }
        var timeout = await RunExpectedAsync("negative", "timeout", "FAILED");
        Check(timeout!.Steps.Any(s => s.StepId == "timeout_cleanup") && timeout.Steps.Any(s => s.Message.Contains("超时")), "超时后执行CLEANUP并保存诊断");
        foreach (var id in new[] { "array_mismatch", "array_length", "empty_array", "any_array_fail", "boolean_mismatch", "text_mismatch", "divide_zero" })
            await RunExpectedAsync("negative", id, "FAILED");
        _dialogs.Required = 42;
        await RunExpectedAsync("negative", "required_parameter", "PASSED");
    }

    private async Task VerifyPauseStopAsync()
    {
        await SelectAsync("negative", "cancel");
        var run = _model.RunSequenceCommand.ExecuteAsync(null);
        await Until(() => _model.Rows.Any(r => r.Step?.Id == "cancel_wait" && r.Status == "RUNNING"), TimeSpan.FromSeconds(15));
        await _model.PauseResumeCommand.ExecuteAsync(null);
        await Until(() => _model.ExecutionState == "PAUSED", TimeSpan.FromSeconds(10));
        await Task.Delay(1200);
        Check(_model.ExecutionState == "PAUSED" && !run.IsCompleted, "实际暂停并维持暂停状态"); Capture("paused");
        await _model.PauseResumeCommand.ExecuteAsync(null);
        await Until(() => _model.ExecutionState == "RUNNING", TimeSpan.FromSeconds(10));
        Check(!run.IsCompleted, "暂停后继续运行");
        var clock = Stopwatch.StartNew(); await _model.StopSequenceCommand.ExecuteAsync(null); await run;
        Check(_model.ExecutionState == "STOPPED" && clock.Elapsed.TotalSeconds < 8, "停止等待中的序列并退出Host");
        Check(_model.CanEditSequence, "运行停止后恢复编辑");
    }

    private async Task VerifyHostGuardsAsync()
    {
        var simulation = new SimulationRepository(sample).Load().ToDictionary(p => p.Key, p => SequenceValueOperations.Unwrap(p.Value));
        var valid = new SequenceDefinition { Id = "guard", Name = "Host防护验收", Role = "main", Steps = [AcceptanceProject.Step("guard_call", "call_sequence", "非法调用主序列", new { sequence_id = "target" })] };
        var target = new SequenceDefinition { Id = "target", Name = "不能被调用的主序列", Role = "main", Steps = [AcceptanceProject.Step("target_comment", "comment", "不会执行")] };
        Check(!SequenceLibraryPreflight.Validate(valid, [valid, target]).IsValid, "预检拒绝调用主序列");
        var result = await new OpenTapHostClient().RunAsync(valid, [valid, target], new Dictionary<string, object?>(), simulation);
        Check(result.Result.Status == "failed" && result.Result.Error.Contains("主序列"), "OpenTAP编译器独立拒绝调用主序列");
        target.Role = "sub"; target.Steps = [AcceptanceProject.Step("recurse", "call_sequence", "递归调用", new { sequence_id = "target" })];
        Check(!SequenceLibraryPreflight.Validate(valid, [valid, target]).IsValid, "预检拒绝递归调用");
        result = await new OpenTapHostClient().RunAsync(valid, [valid, target], new Dictionary<string, object?>(), simulation);
        Check(result.Result.Status == "failed" && result.Result.Error.Contains("递归"), "OpenTAP编译器独立拒绝递归调用");
        valid.Steps = [AcceptanceProject.Step("break_outside", "break", "循环外BREAK")];
        Check(!SequenceLibraryPreflight.Validate(valid, [valid]).IsValid, "预检拒绝循环外BREAK");
        result = await new OpenTapHostClient().RunAsync(valid, [valid], new Dictionary<string, object?>(), simulation);
        Check(result.Result.Status == "failed", "OpenTAP编译器拒绝循环外BREAK");
        File.WriteAllText(Path.Combine(output, "guard-last-events.json"), JsonSerializer.Serialize(result.Events, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task SelectAsync(string projectId, string sequenceId)
    {
        if (_model.SelectedSubproject?.Id != projectId) _model.SelectedSubproject = _model.SequenceSubprojects.Single(p => p.Id == projectId);
        if (_model.SelectedSequence?.Id != sequenceId) await _model.SelectSequenceAsync(_model.Sequences.Single(s => s.Id == sequenceId));
        if (_model.SelectedSequence?.Id != sequenceId) throw new InvalidOperationException("无法选择序列: " + sequenceId + " " + _model.Status);
    }
    private async Task<SequenceRunResult?> RunExpectedAsync(string project, string sequence, string state)
    {
        await SelectAsync(project, sequence);
        var task = _model.RunSequenceCommand.ExecuteAsync(null);
        await task.WaitAsync(TimeSpan.FromSeconds(25));
        Check(_model.ExecutionState == state, sequence + "实际执行结果为" + state);
        if (!_model.SelectedSequence!.IsMain) return null;
        var repo = new RunResultRepository(sample);
        var summary = repo.LoadSummaries().FirstOrDefault(s => repo.LoadResult(s.Id)?.SequenceId == sequence);
        Check(summary is not null, sequence + "保存正式运行报告");
        return repo.LoadResult(summary!.Id);
    }
    private void Reject(Action action, string name)
    {
        var rejected = false;
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or FormatException or OverflowException) { rejected = true; }
        Check(rejected, name);
    }
    private static async Task Until(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition()) { if (clock.Elapsed > timeout) throw new TimeoutException("等待条件超时"); await Task.Delay(30); }
    }
    private static void Click(Button button)
    {
        button.BringIntoView();
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        if (button.Command?.CanExecute(button.CommandParameter) == true) button.Command.Execute(button.CommandParameter);
    }
    private static IEnumerable<T> Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (var descendant in Find<T>(child)) yield return descendant;
        }
    }
    private void Capture(string name)
    {
        _window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)_window.ActualWidth, (int)_window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(_window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
    private static void CopyProject(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(f => !Path.GetRelativePath(source, f).StartsWith("reports" + Path.DirectorySeparatorChar)))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true);
        }
    }
}

internal sealed class AcceptanceDialogs : ISequenceDialogService
{
    public string? Name { get; set; }
    public int Required { get; set; } = 42;
    public int ParameterCount { get; private set; }
    public Task<SequenceUnsavedChoice> AskUnsavedSequenceAsync(string name) => Task.FromResult(SequenceUnsavedChoice.Save);
    public Task<bool> ConfirmDeleteSequenceAsync(string name) => Task.FromResult(true);
    public Task<string?> RenameSequenceAsync(string name) => Task.FromResult(Name);
    public Task<bool> ConfirmStopExecutionAsync() => Task.FromResult(true);
    public Task<IReadOnlyDictionary<string, JsonElement>?> RequestRunParametersAsync(string name, IReadOnlyList<ParameterInputRow> parameters)
    {
        ParameterCount = Math.Max(ParameterCount, parameters.Count);
        var values = new Dictionary<string, JsonElement>();
        foreach (var row in parameters)
        {
            if (row.Definition.Default is null) { row.Mode = "常量"; row.Value = Required.ToString(); }
            if (row.ReadValue() is { } value) values[row.Name] = value;
        }
        return Task.FromResult<IReadOnlyDictionary<string, JsonElement>?>(values);
    }
}
