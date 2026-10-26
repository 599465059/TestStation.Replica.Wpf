# TestStation.OpenTapHost

WPF 保留自有 SequenceDefinition 格式和序列编辑器；本项目是独立的 OpenTAP 9.35.0 执行进程，目标框架为 net9.0。WPF 本身继续使用 .NET 10。

## WPF 接入与部署

- WPF 运行按钮已改为调用 `Services/OpenTapHostClient.cs`，每次运行启动一个独立 Host 进程，发送当前入口序列、活动序列子工程的序列库、运行参数及模拟数据。有参数的序列运行或调试前打开类型化输入窗口，本次输入不修改默认值。
- 子序列调用属性按目标声明提供常量、变量引用或默认值编辑；运行前校验可达调用图。自动测试内增加运行过程、变量/通道、结果表和历史报告面板，并支持命名模拟场景。
- 暂停、继续、停止通过同一 stdin 管道发送；界面接收 stdout 事件更新步骤高亮、步骤状态、日志和运行汇总。执行子序列时自动切换到对应序列并滚动到当前高亮步骤，支持多层调用；返回时跟随调用者，暂停时保留高亮，结束或停止后返回本次运行入口。使用 sequence_id 区分不同序列中相同的步骤 ID，切换时保留各序列的步骤状态。
- 暂停为协作式暂停，在步骤边界、WAIT 和循环检查点生效。收到 run_paused 后界面才显示暂停；暂停时间不计入 WAIT 的等待时长或步骤超时。后续阻塞设备调用也需要提供检查点/取消支持。
- 停止后最多等待 5 秒，仍未完成则结束 Host 进程树。协议错误、Host 启动失败或异常退出会进入失败结果，并显示 stderr 中的诊断信息；WPF 不回退到旧执行器。
- 主序列的原报告格式及 HTML 报告继续保存到工程 `reports` 目录；额外的 `<run_id>.opentap.json` 保存 Host 原始事件，包括结果表、调用路径和每次循环的 step_run_id。
- WPF 项目已添加 Host 构建依赖，正常 build/publish 会将 Host 运行文件及插件目录放入 WPF 输出的 `OpenTapHost` 子目录，不向 WPF 引用 OpenTAP 程序集。
- 根目录快捷启动文件会恢复依赖、构建变更项目，再启动 WPF。这里只修改启动配置，本轮没有执行该文件。
- Host 设置 `RollForward=Major`：优先使用 .NET 9；未安装 9 时允许使用 .NET 10。此配置不代表已经完成 .NET 10 环境运行验证。
- 如需指定另一个部署位置，可设置环境变量 `TESTSTATION_OPENTAP_HOST` 为 Host 的 DLL 或 EXE 完整路径；DLL 旁需保留 runtimeconfig、deps 和全部运行依赖。

## 当前接入范围

- 软件工程下包含多个序列子工程，WPF 每次仅发送活动标签页的序列库。序列的 `role` 为 `main` 或 `sub`；编译器拒绝任何以主序列为目标的调用。
- WPF 可运行主序列或单独调试子序列。是否持久化报告由运行入口决定；调试子序列不写报告和历史，主序列调用的子序列仍正常贡献结果。
- 请求新增 `global_definitions`、`station_definitions` 数组，以及 `global_values`、`station_values` 字典。共享变量放在帧外，局部变量和参数每次调用重新初始化。
- 表达式、变量引用和 STATEMENT 目标支持 `Locals.name`、`Globals.name`、`Station.name`；原局部变量裸名称继续兼容。
- 初始化和共享变量赋值后发布 `shared_values`，快照和最终事件也含 `globals`、`station`。WPF 按子工程保存全局运行值，按软件会话保存站变量运行值，下一次 Host 运行时传入；不写回变量默认值。

- SequenceCompiler 保留 Setup/Main/Cleanup、THEN/ELSE、步骤公共属性、变量声明和子序列边界。
- IF 按表达式选择一个分支；不会修改步骤原有的 Enabled 状态。
- FOR 支持正负步长、表达式参数和最大次数（默认 1000），离开循环时恢复原循环变量。
- WHILE 每次重新计算条件，并检查最大次数和取消请求。
- BREAK/CONTINUE 作用于当前序列中最近的循环，可从 IF 内触发；不会越过子序列边界。
- 控制流指令通过运行上下文传递。每个选中的步骤仍由 OpenTAP RunChildStep 执行，不依赖异常跨越引擎边界。
- STATEMENT 使用现有表达式解析器给已声明变量赋值，例如 i + 1。数字、布尔、引号字符串、变量引用、括号和逻辑比较共用同一语法。
- 子序列在独立变量作用域内运行，可通过 arguments 传入已声明的 parameter 变量，并执行自己的 Setup/Main/Cleanup。
- 编译阶段拒绝递归调用、重复步骤 ID、非法子分支及循环外的 BREAK/CONTINUE。
- WAIT、循环和步骤入口/出口支持协作式取消与超时检查。后续设备驱动需要配合取消令牌实现阻塞 I/O 的中断。
- SET VALUE / READ VALUE / ASSERT 当前使用 simulation_values 对应的模拟通道；真实设备插件尚未接入。
- SET VALUE / READ VALUE 支持 `params.signals` 表格数组，每行独立指定通道、数据类型、常量或表达式输入。READ VALUE 可写入局部、全局或站变量，并按标量/一维数组执行相等、范围、容差、文本和正则比较。
- 步骤支持 `before_expression`、`after_expression`，使用共享表达式解析器执行变量赋值；支持数学函数、数组字面量和下标。

## 失败处理

- CONTINUE：继续后续步骤，失败判定保留在最终结果中。
- GOTO_CLEANUP：结束当前序列主体，执行当前序列 Cleanup；子序列调用返回失败后，由调用步骤的失败动作决定上层流程。
- STOP：停止主体步骤，按调用链执行 Cleanup 并退出。

每个序列边界的 Cleanup 执行一次，且不受主体已到期的超时限制。用户强制取消仍由 OpenTAP 终止执行，不承诺执行完所有清理步骤。

## JSON Lines 协议

标准输入/输出每行一个 JSON 对象。输出只用于协议，插件 Console 输出转到 stderr。

运行请求可省略 command，保持与原请求兼容：

```json
{
  "command": "run",
  "sequence": { "id": "main", "name": "主序列", "steps": [] },
  "sequences": [],
  "parameters": {},
  "simulation_values": {}
}
```

运行时仍能读取控制命令：

```json
{ "command": "stop" }
```

暂停、继续分别使用 `{"command":"pause"}`、`{"command":"resume"}`。

事件类型：plan_compiled、run_started、run_paused、run_resumed、step_started、step_finished、execution_snapshot、shared_values、log、result、run_finished、error、run_completed。

execution_snapshot 在步骤和循环检查点发布当前调用栈、循环次数、当前序列变量、已读通道与模拟值。结果监听器仍负责步骤状态和 OpenTAP 结果表；快照从执行线程发布，避免监听器异步回调读取已切换的变量作用域。

请求可携带 run_id；WPF 会生成本次运行 ID。run_finished 提供 OpenTAP 结果，run_completed 表示 Host 执行任务（含结果监听器）已结束，客户端收到后关闭输入并等待 Host 退出。输入管道提前关闭时 Host 会取消活动运行。

所有运行事件使用同一个 run_id；step_id 对应源文件 ID，step_run_id 区分每次循环中的实际执行，parent_step_run_id、sequence_id、section、path 用于定位调用层级。record_result=false 时仍回传实时状态，但不把该步骤结果加入最终 results，也不输出其结果表。

已完成实际 WPF + OpenTAP 验收：正常主序列运行约 66 秒，另覆盖编辑、变量、异常、暂停与取消等路径，见 `docs/自动测试全功能验收记录.md`。组合步骤显式声明允许的子步骤类型，支持 OpenTAP 的父子步骤约束。真实设备插件尚未接入；纯软件操作说明见 `docs/纯软件序列使用说明.md`。
