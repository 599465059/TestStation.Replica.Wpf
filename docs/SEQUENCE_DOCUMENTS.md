# 序列文档与运行保护

实施日期：2026-10-06。工程：TestStation.Replica.Wpf，.NET 10 + Prism + MVVM。

## 行为

- 工具栏提供撤销/重做、保存及未保存状态；编辑面板标题使用 * 标记。Ctrl+Z/Y/S 对应文档撤销/重做/保存，文本框保留自身的文字撤销。
- 每个文档独立保留最多 100 个修改快照。插入、移动、子树删除、剪切/粘贴、Enabled、名称/描述/参数、变量均计入历史。移动和恢复 IF/FOR 时保留 THEN/ELSE 及后代结构。
- 保存包含未“应用”的有效输入；无效 JSON、时间或变量不提交、不保存、不放行。切换属性行前提交有效输入，无效时留在原行并保留表单。
- 新建、切换序列、切换工程和退出均处理未保存状态。保存成功才更新保存基线；放弃恢复最后一次保存，未保存的新文档则移除；取消保留原文档及输入。
- 重命名保持序列 ID 与引用；复制保留内容、分支和变量，所有 ID 重新生成，副本尚未保存。删除前检查其他序列调用并通过 Industrial.Controls DialogHost 确认，成功后删除准确的 .tsseq 文件。
- 运行先提交有效输入，执行引擎使用副本。运行、暂停、停止后的 CLEANUP 全部覆盖编辑锁，UI 与直接命令/API 均检查锁定。取消关闭不会停止运行；确认停止则等待清理、结果保存和运行任务结束后再继续。
- 工程的序列和模拟值先读取再替换当前文档，读取失败保留当前工程。工程及序列文件格式不变。

## 架构

| 层 | 实现 |
|---|---|
| Application | ISequenceDocumentService / SequenceDocumentSession：保存基线、事务、快照、撤销重做、复制；SequenceEditor 保持唯一结构规则 |
| App ViewModel | MainViewModel.Documents：未保存决策与管理；Editors：输入校验和提交；Execution：执行任务与状态；主文件：工程入口和结构编辑 |
| App UI | SequenceInteractionService 处理快捷键/鼠标；MainWindow 处理工程及关闭事件；ProtocolDialogService 复用发布包 DialogHost |

Domain/Application 不引用 WPF 或控件包。工业控件库、原 Python 和参考源码没有作为写入目标。

## 验证

```powershell
dotnet build TestStation.slnx --no-restore
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui
# 单独复验文档操作与运行中关闭：
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui-documents
# 验证正式 Prism 可执行文件和实际注册的弹窗服务：
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src/TestStation.Smoke/Verify-PrismStartup.ps1 -ProjectPath "F:\opencode\TestStation.Replica.Wpf\artifacts\demo-project" -VerifyUnsavedProtection
```

业务回归包含嵌套/跨组撤销重做、保存基线、失败事务回滚、重做失效、复制 ID 与引用隔离、管理、三种未保存决策、无效输入、保存失败恢复、损坏工程读取、运行/暂停/CLEANUP 锁定、即时停止和报告保存。

窗口回归通过实际鼠标/键盘验证工具栏、Ctrl+Z/Y/S、文字撤销、控件库弹窗、切换工程、Alt+F4/标题栏关闭取消与保存，以及暂停运行时取消关闭/停止并关闭。既有拖拽、停靠、主题和协议测试继续使用完整 --ui 入口。

## 当前边界

历史只在本次运行的内存中保留，不写入 .tsseq；没有新增崩溃自动恢复。已经确认的序列文件删除不能撤销，提示明确说明。模拟值应用后直接保存到原模拟文件，未提交表单在离开工程时处理；它不属于序列撤销历史。无硬件通信扩展。

## 停止与清理（2026-11-01）

OpenTAP 执行器：点“停止”后，当前序列的主体在下一个检查点结束，然后执行 CLEANUP 段。清理段不受停止影响（等待步骤照常等待，设备读写不随停止取消），某一步失败不跳过后续清理步骤。主程序等待执行器结束的时间在设置页“执行器 → 停止等待时间”中设置（默认 15 秒），超时后强制结束执行器进程。电源设备在会话关闭前还会单独发送关闭输出命令（设置页“安全”）。实现说明见 docs/UI_LAYOUT_PLAN.md 第 10 节。
