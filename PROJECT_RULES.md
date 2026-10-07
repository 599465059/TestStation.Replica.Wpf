# Test Station 项目最高规则

本文档仅适用于当前独立的 C# + WPF 工程：`F:\opencode\jmytest\TestStation.Replica.Wpf`。它是该工程 UI、模块和主题实现的最高约束，不是本次对话的全局规则，也不适用于 `F:\opencode\web_py\test`、`F:\opencode\web_py\WpfHilPlatform` 或其他项目。参考工程中的代码只能择优适配，不能覆盖本项目的领域模型、工程文件格式和分层边界。

## 1. UI 技术栈

- 目标框架统一使用 .NET 10：App/Smoke 为 `net10.0-windows`，Domain/Application/Infrastructure 为 `net10.0`。开发 SDK 由本工程的 `global.json` 管理；领域层和应用层继续保持无 WPF 依赖。
- 应用架构使用 `Prism.DryIoc`，按模块注册依赖、页面和服务。
- 基础 UI 控件统一使用用户提供的 `Industrial.Controls` 已发布包。库内缺少的控件、样式、状态或接口必须登记到 `docs/CONTROL_LIBRARY_GAPS.md`；不得修改 IndustrialKit，也不得在应用另造基础模板。
- `Button`、`TextBox`、`DataGrid`、`TreeView`、`ListBox`、`Menu` 等可以保留 WPF 控件类型，但必须使用 Industrial.Controls 提供的样式；库未封装的类型仅可使用缺项清单中明确记录的集中 HandyControl 回退样式。禁止默认 WPF 外观和自行替换基础控件的 `ControlTemplate`；库没有替换模板的分隔线等类型也必须使用库的样式并接入项目主题。保留现有主窗口外壳和 AvalonDock 停靠模板。
- 图标统一使用 `MaterialDesignThemes.Wpf` 的 `PackIcon` 和 `PackIconKind`。页面不得继续新增 Unicode 图标、Segoe Fluent Icons 字体图标或手绘 SVG；已有图标迁移时逐步替换。
- 停靠、浮动、自动隐藏和面板分割继续使用现有 `Dirkster.AvalonDock`，不得为页面另写停靠实现。
- 第三方控件不能直接进入领域层或应用层，必须留在 View、Controls 或 UI 模块中。

## 2. 控件和页面组织

- 页面 (`Views`) 只负责布局、绑定和交互组合，不重复定义通用控件的视觉样式。
- 当前阶段不自研基础控件库。业务页面的 `UserControl` 负责组合 Industrial.Controls 提供的控件和样式；后续用户提供的自定义库按授权范围接入。
- 用户提供的 `Industrial.Controls` 通过本工程 `vendor/nuget` 的固定版本包接入，允许使用该库已封装的控件和模板；不得在页面中重写或复制其基础模板。当前锁定最新已发布 `1.2.0`，正式页面与预览使用同一资源入口。`F:\opencode\web_py\UI库\IndustrialKit` 源码只读。
- 控件库版本更新必须发布新的包版本，不能覆盖同版本包。引用、主题适配和控件使用分离；不得引用 Gallery 工程或使 Domain/Application 依赖 UI 库。
- 通用控件样式统一放在 `Themes/HandyControlStyles.xaml`，库资源由 `Themes/ResourceBootstrap.cs` 按依赖顺序加载并注册原包样式别名，外壳样式放在 `ShellStyles.xaml`，序列行模板放在 `Themes/EditorTemplates/StepListResources.xaml`。页面不复制这些视觉定义。控件不得反向依赖设备驱动、序列执行器或具体页面。
- 公共按钮、表格、输入框、属性编辑器、工具栏、状态标记和空状态视图只能有一套可复用实现。页面需要通过属性、模板或数据绑定改变内容，而不是复制一份 XAML。
- ViewModel 不直接创建 WPF 控件，不操作控件树；跨页面行为通过 Prism 服务、命令和消息通信完成。
- 业务步骤、设备、协议和执行状态的数据模型必须留在 Domain/Application 层，不能放入控件代码后置文件。
- 序列的唯一正式模型为 `SequenceDefinition` / `SequenceStep`，编辑规则由 Application 层 `ISequenceEditingService` 调用 `SequenceEditor` 完成；不引入参考工程的 `TestStep`、`DocumentViewModel` 或插件协议。
- 序列变更必须经过 ISequenceDocumentService 的文档事务并由 ISequenceEditingService 执行结构操作；禁止 View、交互服务或 Enabled 绑定直接写入模型。属性和变量输入在验证通过后提交，保存失败不得清除未保存状态。
- 新建/切换序列、切换工程、关闭窗口必须处理未保存的模型与表单；用户取消后保留原文档及输入。运行/暂停/CLEANUP 期间同时禁用界面编辑入口并在命令/API 中检查锁定。切换工程和关闭须等待运行任务、清理及报告保存完成。
- 序列复制必须生成独立的序列和全部嵌套步骤 ID；文件删除必须确认并检查调用引用。撤销重做针对当前文档修改，不包含已确认的磁盘文件删除。
- `IAutoTestDisplayService`、`ISequenceDropTargetService` 属于 App 层的显示适配，输出显示行和插入目标；`ISequenceInteractionService` 负责 WPF 命中测试、拖拽、鼠标和键盘事件。它们通过 ViewModel 调用应用服务，不直接修改文档。Domain/Application 不依赖 WPF、HandyControl、MaterialDesign 或 AvalonDock。

## 3. 主题和颜色

- 界面默认使用中文。导航、菜单、面板标题、表头、属性标签、操作按钮、提示和运行状态使用中文；流程关键字 `IF`、`ELSE`、`FOR`、`THEN`、`END IF`、`END FOR` 保留英文。CAN、DBC、SCPI、Modbus 等协议名、单位和变量标识保留原文。
- 显示文字集中维护于 `src/TestStation.App/Localization/UiText.resx`，通过 `UiText` 访问。内部状态码、步骤类型、参数键、协议字段及用户自定义名称不能使用翻译文案作为标识。
- 下拉框通过显示模板翻译选项，绑定和保存继续使用原始标识。已有序列中的默认英文步骤名称只在显示层转换；用户命名和工程格式继续由业务模型管理。

- 主题分为浅色和深色，主题切换必须通过统一的 `IThemeService` 和动态资源完成。页面禁止直接写死主题颜色。
- 主题至少定义以下语义资源：窗口背景、内容背景、提升表面、边框、主文字、次文字、禁用文字、选中背景、焦点边框、悬停背景、错误、警告、成功和信息。
- 浅色主题使用高对比的中性背景和深色文字；深色主题使用低眩光深色背景和浅色文字。所有文字、图标、输入框和表格状态必须分别在两种主题下检查可读性。
- 强调色与明暗主题分离。强调色只负责主按钮、选中项、焦点和主要交互状态，不改变窗口明暗基础色，也不替代错误、警告、成功等语义色。
- 强调色由统一主题服务更新语义资源，默认蓝色，并预留蓝、青、紫、橙等方案。切换强调色不应修改业务数据、布局或控件尺寸。
- 颜色资源使用 `DynamicResource`，不得在页面和控件模板中散落十六进制颜色值。
- 主题服务必须同步 HandyControl 皮肤、项目语义画刷、列表/树的活动和非活动选中状态；弹出菜单及浮动面板也使用同一套资源。深浅主题和强调色分别测试，禁止只检查当前获得焦点的控件。
- Industrial.Controls 的资源契约由 `IIndustrialControlsThemeAdapter` 映射到现有主题，再由 `IThemeService` 更新稳定画刷并发送 `Changed` 通知。ResourceBootstrap 全局按依赖顺序加载原包样式，不能调用库的全局 `Theme.Initialize/Apply` 覆盖本项目配色。自绘控件在主题变化时通过 View 重绘；事件订阅随 View 加载/卸载管理。

## 4. 控件尺寸和布局

- 控件采用统一的高度、内边距、圆角、边框和字体层级；尺寸由公共主题字典集中管理。
- 内容不足时允许留白，内容超出时显示滚动条或让用户调整分割线；不得通过压缩输入框、表格列或文字来隐藏内容。
- 所有表格默认允许用户调整列宽；停靠面板必须设置合理的最小宽度和最小高度。
- 自动测试页面必须保持主窗口内停靠布局，步骤库、步骤表格、序列/变量、属性和运行日志均为可调整面板。
- 运行、预检、暂停/继续、停止及序列保存属于自动测试页面，在该页面内部使用发布包 `KitToolBar` 和公共紧凑按钮样式。主窗口工具栏仅提供工程及全局操作；页面切换后不能在其他模块露出序列执行按钮。

## 5. 迁移和验收

- 手动测试自由画布的位置和尺寸必须以整数网格保存，移动及缩放强制吸附；辅助网格仅在移动/拉伸手势进行时显示。当前允许组合现有库样式实现一个临时数值业务视图和专用画布编辑层供测试，不另建基础控件模板，不修改 IndustrialKit；正式仪表控件的缺项记录后交由用户完善库。

- 协议功能使用独立 `ProtocolModule` 注册；Domain 放协议/帧/字段/DBC 模型，Application 放编辑和预览规则及接口，Infrastructure 放文件与 DBC 解析实现，App 放各页面和 ViewModel。新增后端代码不得依赖 WPF、控件库、图标或停靠组件。
- 协议使用 `.tsproto` v1 和原工程目录约定；一个协议文件包含多个帧。帧过滤仅改变显示，不能删除隐藏帧。字段编辑在副本校验通过后提交，字节重叠和越界必须拒绝。
- 控件迁移例外以 `docs/CONTROL_LIBRARY_GAPS.md` 为准；协议已复刻/原占位/未扩展能力以 `docs/PROTOCOL_REPLICA_MATRIX.md` 为准，不能将占位声明为已完成功能。

- 新页面必须先通过 Prism 模块注册，再接入主窗口导航。
- 新增控件必须有浅色、深色和至少一种强调色下的视觉检查。
- UI 迁移不得破坏现有 Domain/Application API、序列文件格式、拖拽嵌套规则和执行引擎。
- 每次 UI 迁移必须通过 `dotnet build TestStation.slnx --no-restore` 和 Smoke 检查；涉及交互时还要启动窗口验证导航、停靠、拖拽和主题切换。
- 自动测试 UI 的可重复验收入口为 `dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui`，包含实际鼠标拖拽、键盘快捷键、主题状态及停靠恢复。

