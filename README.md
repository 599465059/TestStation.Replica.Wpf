# Test Station — C# / WPF

独立工程，采用 Prism.DryIoc + MVVM + 分层模块化。自动测试编辑器嵌入主窗口，使用用户提供的 Industrial.Controls 样式（未封装类型按缺项清单回退）、MaterialDesign 图标和 AvalonDock 停靠面板；自有序列模型由独立 OpenTAP Host 执行。

## 启动

在本工程目录中运行：

```powershell
dotnet build TestStation.slnx --no-restore
.\src\TestStation.App\bin\Debug\net10.0-windows\TestStation.App.exe
```

首次启动先创建或打开工程目录，再点击左侧“自动测试”。工程文件夹中的 `project.json`、`sequences/` 和其他子目录仍使用原有格式。

也可以直接打开工程：

```powershell
.\src\TestStation.App\bin\Debug\net10.0-windows\TestStation.App.exe --open "你的工程目录"
```

尚未还原 NuGet 包时，先运行 `dotnet restore TestStation.slnx`。开发环境为 Windows + .NET 10 SDK（10.0.200 或更新的稳定版 10.0 SDK），运行环境需要 .NET 10 Desktop Runtime。`global.json` 将本工程的 SDK 限定在 .NET 10，并允许使用同一主版本下更新的 SDK 补丁与功能带。

.NET 10 升级范围、兼容调整和验证结果见 [docs/NET10_UPGRADE.md](docs/NET10_UPGRADE.md)。

验收示例工程位于 `artifacts/demo-project`，包含嵌套编辑示例和可运行的模拟序列，启动后可以通过打开工程目录查看。

最新全功能验收工程位于 `examples/自动测试全功能验收`，正常主序列包含 65 秒等待。双击其中的 `打开验收工程.cmd` 可准备专用站变量并打开工程。重复自动验收的命令与覆盖范围见 [自动测试全功能验收记录](docs/自动测试全功能验收记录.md)。

## 控件库预览

从顶部“视图 → 控件预览”进入，不需要先打开工程。工作区左侧“系统设置”下方也有“控件预览”入口。

此页使用固定版本 `Industrial.Controls 1.0.0` 的样式和控件，包含输入、按钮、下拉框、可调列宽表格、树、范围滑块、旋钮、仪表、进度环及颜色选择。使用标题栏太阳按钮和色点比较深浅主题及强调色。样例使用独立 ViewModel，不会修改工程和序列。

控件包随工程保存在 `vendor/nuget`，无需引用原始源码目录。正式页面的可迁移控件已使用同一包；未封装控件及主题接口见 docs/CONTROL_LIBRARY_GAPS.md。接入边界、主题适配、升级步骤和验收记录见 [docs/INDUSTRIAL_CONTROLS_INTEGRATION.md](docs/INDUSTRIAL_CONTROLS_INTEGRATION.md)。

正式页面与预览页的实际控件来源、保留的停靠模板及主题遗漏修复见 [docs/CONTROL_STYLE_AUDIT.md](docs/CONTROL_STYLE_AUDIT.md)。

## 编辑器操作

界面采用中文：面板、表头、属性、按钮、菜单和运行状态统一显示中文；`IF`、`ELSE`、`FOR`、`THEN` 及结束标记保留英文。变量标识、协议术语和用户自定义名称保留原文。文字资源位于 `src/TestStation.App/Localization/UiText.resx`，下拉框显示中文选项，序列和协议文件保存原始英文标识。

- 从左侧步骤库拖拽步骤到表格，或双击添加到当前选择位置。
- IF、THEN、ELSE、END IF 显示为明确的行；IF 和 ELSE 对齐，分支内容缩进，FOR/IF 可以继续嵌套。
- 拖到 ELSE 上半行添加到 THEN 尾部，拖到下半行添加到 ELSE 开头；插入线标明落点。
- 拖拽容器步骤会保留全部子步骤，可以跨 SETUP、MAIN、CLEANUP 移动；不能放入自身或自身的后代中。
- 点击三个分组标题展开/收起。Ctrl/Shift 支持多选；Delete、Ctrl+C/X/V、Ctrl+↑/↓ 支持删除、复制、剪切、粘贴和移动。
- Ctrl+Z / Ctrl+Y 撤销和重做，包含嵌套步骤、移动、删除、启用状态、属性和变量；最多保留当前文档最近 100 次修改。文本框获得焦点时，Ctrl+Z/Y 优先编辑文字。
- Ctrl+S 保存当前序列，包括尚未点击“应用”的有效属性和变量输入；输入无效时保持修改并显示原因。
- 标题的 * 和工具栏“未保存”标记用于提示文档状态；新建、切换序列、切换工程和关闭窗口会提供保存、放弃、取消。
- 右侧序列列表下方提供重命名、复制和删除。复制保留全部分支和变量，重新生成序列及步骤 ID；删除需确认，被其他序列调用时先阻止删除。文件删除不能撤销。
- 运行及暂停期间，序列编辑、选择、管理和模拟值编辑均锁定；停止后 CLEANUP 完成前仍保持锁定。切换工程和关闭窗口须先确认停止，并等清理及报告保存结束。
- 表格前两列固定，其他列可调整宽度；最后一列填充剩余空间。
- 拖动停靠标题可以移动或浮动面板，拖动分隔线可以调整大小。隐藏后通过“视图 → 恢复自动测试默认布局”恢复。
- 属性区初始约占下方三成空间，超出显示范围时出现滚动条，保持输入控件高度。
- 上方横向默认比例为步骤库 14%、序列表格 66%、右侧面板 20%；最大化后按比例分配，受各面板最小宽度约束。所有分隔线仍可拖动调整。
- 顶部太阳图标切换明暗主题，三个色点切换蓝、紫、青强调色。
- 主窗口工具栏只放工程操作。自动测试页顶部使用控件库的紧凑 ToolBar，提供新建/保存序列、运行、预检、暂停/继续、停止及运行状态；切换其他页面后随自动测试页面隐藏。暂停后按钮文字和图标自动切换为“继续”。标题栏不再提供页面搜索框。

当前工作范围是自动测试编辑体验与现有模拟执行；其他导航中的占位页及新的硬件驱动不属于本轮扩展。

手动测试当前只保留工区标签、标签/窗口/平铺/层叠布局和插件界面宿主。插件通过 `IManualPluginViewProvider` 提供界面，由 `IManualPluginViewRegistry` 注册；同一工区可用“标签查看”或“平铺查看”同时查看多个插件界面。插件目录扫描、安装包管理以及设备通道与 OpenTAP 步骤的实际注册仍属于后续插件系统工作。

## 分层

| 项目 | 责任 |
|---|---|
| `TestStation.Domain` | 工程、序列、步骤和变量等正式数据模型 |
| `TestStation.Application` | 工程存储、序列编辑规则、子树剪贴板、校验和执行引擎 |
| `TestStation.App` | Prism 注册、ViewModel、显示/落点适配、WPF 交互、主题和页面 |
| `TestStation.Smoke` | 文件兼容、编辑/执行回归、实际窗口交互验收 |

应用层不依赖 UI 库。页面事件交给交互服务，文档变更统一经过应用层编辑服务。ISequenceDocumentService 管理文档快照、保存基线和撤销重做；ViewModel 的 Documents、Editors、Execution 分别负责文档操作、表单校验和运行适配。

## 验证

```powershell
dotnet build TestStation.slnx --no-restore
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui
```

UI 验收会打开窗口并实际操作鼠标、键盘，完成后关闭测试窗口并恢复鼠标位置。截图输出到 `artifacts/ui/editor-light.png` 和 `editor-dark.png`。

实际 Prism 程序启动、导航和最大化验证也可单独运行（默认在完成后关闭窗口，添加 `-KeepOpen` 则保留）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src/TestStation.Smoke/Verify-PrismStartup.ps1 -ProjectPath "F:\opencode\jmytest\TestStation.Replica.Wpf\artifacts\demo-project"
```

本项目最高规则见 [PROJECT_RULES.md](PROJECT_RULES.md)，本轮迁移与验收记录见 [docs/AUTO_TEST_INTEGRATION.md](docs/AUTO_TEST_INTEGRATION.md)，来源说明见 [NOTICE.md](NOTICE.md)。

总体模块依赖和分层边界见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，当前及计划中的应用、驱动和插件接口见 [docs/INTERFACE_CATALOG.md](docs/INTERFACE_CATALOG.md)。

设备、通信资源、通道映射和轮询策略的当前文件格式见 [docs/RESOURCE_MODEL.md](docs/RESOURCE_MODEL.md)。

## 协议管理

打开工程后使用左侧四个子节点：协议库、自定义协议、DBC 数据库、协议测试。

- 在协议库新建、删除、导入或导出 .tsproto；在自定义协议中编辑名称、ID、版本、通信类型和多个命名帧。
- 选择协议树根比较全部帧，选择子帧只看一帧；选中空字节后双击字段库添加，也可拖入和拖动已有块。字段不能重叠或越界。
- 属性可应用或通过保存提交；未保存切换协议时可以保存、放弃或取消。字节宽度默认 54，显示字节数 0 为自动适配，超出后滚动。
- DBC 导入同内容去重，树按发送/接收 ECU 和未分类消息组织；表格所有列可调整宽度。
- 协议测试为离线工具：示例帧填充常量并按字段显示 HEX；DBC 可以通过信号值 JSON 编码和解析。尚未定义的自定义帧长度/校验约定不会自动计算。

功能与原 Python 源码对照及待完成能力见 docs/PROTOCOL_REPLICA_MATRIX.md；控件缺项见 docs/CONTROL_LIBRARY_GAPS.md。

协议示例工程：`artifacts/protocol-demo`，含一个双帧协议和 Python 原测试 DBC。使用上方 --open 启动参数打开该目录，或在初始页选择它。

编译完成后也可以双击本工程的 `start-protocol-demo.cmd` 直接打开这个示例工程。

序列可靠性功能、边界和测试入口见 [docs/SEQUENCE_DOCUMENTS.md](docs/SEQUENCE_DOCUMENTS.md)。

本轮实施和实际验证记录见 [docs/IMPLEMENTATION_VALIDATION.md](docs/IMPLEMENTATION_VALIDATION.md)。

