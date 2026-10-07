# 自动测试融合与验收记录

日期：2026-10-05。实施范围：独立的 `TestStation.Replica.Wpf`，参考 `F:\opencode\web_py\test`。本轮完成编辑体验与主题整合，继续使用既有工程格式及执行引擎。

## 参考内容如何迁移

| 参考内容 | 当前实现 |
|---|---|
| `IndentToMarginConverter` | 保留缩进算法，适配当前 Depth，增加基础偏移；实际用于行模板 |
| 图标映射与分类颜色 | 按当前步骤 Kind/显示名映射 MaterialDesign PackIcon；六类颜色引用当前深浅主题语义资源 |
| `StepListResources.xaml` | 适配为普通步骤、分组、分支、尾部模板，绑定当前显示行 |
| `HandyControlStyles.xaml` | 复用控件状态及紧凑布局思路；集中继承库样式，保留项目颜色 |
| `StepDragDropCoordinator` / `StepSequenceInteractionService` | 按当前目标结构重写拖拽规则与 WPF 事件适配，提供落点线及非法移动预检 |
| `DocumentDisplayCoordinator` | 用 `AutoTestDisplayService` 从 SequenceDefinition 构建显示行 |
| `DocumentStepEditingCoordinator` | 编辑流程由 SequenceEditingService 委托现有 SequenceEditor |

参考模型和主窗口未进入当前工程；原许可与作者归属见 `NOTICE.md`。

## 分层边界

```text
SequenceWorkspace / MainWindow
    → ISequenceInteractionService（鼠标、键盘、命中测试）
    → MainViewModel
    → ISequenceEditingService（应用层，唯一编辑入口）
    → SequenceEditor → SequenceDefinition / SequenceStep

SequenceDefinition → IAutoTestDisplayService → 显示行 → DataTemplate
显示行 + 鼠标位置 → ISequenceDropTargetService → SequenceInsertTarget
IThemeService → 项目语义资源 + HandyControl 皮肤 + 停靠/弹出窗口
```

编辑服务及正式模型位于 Application/Domain，无 WPF 依赖。显示行、落点适配和实际鼠标事件位于 App 层，不另建一套领域模型。服务在 Prism ShellModule 注册，ViewModel 通过注入调用。

## 页面与交互

- 主窗口内 UserControl + AvalonDock，包含步骤库、步骤表格、序列、变量、模拟数据、运行日志、属性共七个停靠面板。
- 横向默认 14 : 66 : 20，三个区域均使用 star 比例；嵌套右侧面板用单一 star 长度，避免最大化时左右抢占序列区。竖向默认 7 : 3。面板最小宽高约束保持有效。
- DataGrid 整行多选，前两列固定，允许调整列宽及排列，STATUS 列填充剩余宽度。
- SETUP、MAIN、CLEANUP 独立折叠，点击分组后保留该区域作为双击添加/粘贴目标。
- IF/ELSE 同层，THEN/ELSE 内容缩进；IF 中可以嵌套 FOR 和 IF，结束行显示 END IF/END FOR。
- 库双击、库拖入、内部移动、跨区域拖拽、分支落点、容器子树保留、非法落点拒绝、自动滚动均进入当前编辑规则。
- Delete、Ctrl+C/X/V、Ctrl+↑/↓ 及右键菜单可用；复制父节点和后代时不会重复复制，粘贴生成新的子树 ID。
- 属性区超出范围出现双向滚动条，输入框保持至少 29 的最小高度。浮动/隐藏面板可由“恢复自动测试默认布局”重新停靠。

## 控件与主题检查

- 基础控件的库样式入口在 HandyControlBases；项目状态样式在 HandyControlStyles，外壳样式在 ShellStyles，业务行模板在 StepListResources。
- 检查 19 种基础类型的库样式链；列表项显式使用 HandyControl 的 ListBoxItemBaseStyle，分隔线使用 SeparatorBaseStyle。WPF 类型保留，但不显示默认 WPF 输入、列表或菜单外观。
- 基础页面没有另写 ControlTemplate、十六进制颜色或 SystemColors；保留的 AvalonDock 模板通过语义画刷适配主题。
- 主题切换同步皮肤及语义颜色；强调色只更新强调/选中资源。活动及失去焦点的树/列表选中背景均接入项目颜色。
- 共享画刷使用可通知的颜色源，避免模板封存和弹出窗口使画刷停留在旧主题。
- 所有步骤图标使用 PackIcon，分类色分别提供深浅主题对比度；标题栏色点按钮也继承 HandyControl 模板。

## 验证结果

执行以下命令：

```powershell
dotnet build TestStation.slnx --no-restore
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui
```

结果：编译 0 警告、0 错误；业务回归及实际窗口验收通过。

| 验收 | 证据 |
|---|---|
| 嵌套结构及文件格式 | 保存/重开 Setup、Main、Cleanup 和嵌套 IF/FOR，验证现有序列封装格式 |
| 编辑边界 | 非法自身/后代目标、分支落点、父子选择去重、完整子树复制、剪切及新 ID |
| 鼠标操作 | 实际鼠标把 FOR 拖到 ELSE 前方、带 COMMENT 子步骤移入 ELSE、跨 Setup 移动及库步骤拖入 Cleanup |
| 双击与分组 | 通过实际控件事件路由添加 WAIT，折叠/展开三个区域，选择 Setup 后双击添加到 Setup |
| 键盘操作 | 实际键盘 Ctrl+C/X/V、Delete、Ctrl+↑/↓，检查文档结构及顺序 |
| ENABLED | 控件自动化取消/重新勾选，确认能够再次启用 |
| 主题 | 连续浅/深/浅/深切换，紫/青强调色切换；检查表格、输入、按钮、标题栏图标、右键菜单及活动/非活动选中背景 |
| 最大化比例 | 普通及最大化窗口中，序列表格占停靠区域至少 58%，正常空间下接近默认 66% |
| 正式 Prism 程序 | 启动实际 EXE、打开验收工程、通过 UI Automation 进入自动测试，最大化后序列区实测约 65.1%；主题按钮及强调色按钮可操作，程序保持响应 |
| 停靠恢复 | 实际浮动窗口的显示源与主窗口分离；隐藏属性区后恢复全部七个面板并重新停靠 |
| 属性滚动 | 面板缩小到 100 时出现垂直滚动条，输入框仍保持原高度 |
| 执行回归 | 既有模拟执行、变量、模拟通道、暂停/停止、调用序列和结果报告回归 |

界面截图：`artifacts/ui/editor-light.png`、`artifacts/ui/editor-dark.png`，使用最大化窗口生成。

正式程序的桌面截图为 `artifacts/ui/prism-maximized.png`。独立启动检查脚本为 `src/TestStation.Smoke/Verify-PrismStartup.ps1`，不替代上面的业务及窗口交互回归。

本轮没有扩展实际硬件驱动、参考工程插件系统或新的执行机制。自动化验证覆盖上述场景，生产现场的真实设备验证仍属于后续阶段。
