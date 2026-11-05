# 界面优化方案（2026-11-01）

依据：`artifacts/ui/editor-failure.png`、`artifacts/automatic-acceptance/20261031-230055/failure.png`、`20261025-204734/signal-table-Light.png`、`artifacts/manual-ui/device-navigation-final.png`、`manual-compact-tile.png`，以及 `Themes/`、`SequenceWorkspace.xaml`、`Services/IThemeService.cs`、`Localization/UiDisplay.cs` 源码。所有改动遵守 PROJECT_RULES.md：只改主题令牌、集中样式和页面 XAML，不改 IndustrialKit，不另造基础控件模板。

## 1. 主要问题和原因

| # | 现象 | 根因（文件） | 影响 |
|---|---|---|---|
| 1 | 深色主题发灰、发闷，面板之间分不出层次 | `UiTheme.Dark.xaml` 中 Window/Content/Surface/Subtle 为 `#30353A/#353C42/#3A4147/#444B52`，相邻两级亮度只差 2–4%，边框 `#555555` 是纯灰，与带蓝调的背景不协调 | 整体观感最差的一条 |
| 2 | 选中行是一整条饱和亮蓝，行内的图标、状态色、复选框都被吃掉（例如“未完成”橙字落在亮蓝底上） | `IThemeService.cs:103` 把 `SelectedRowBrush` 直接设为强调色 | 刺眼，选中行反而最难读 |
| 3 | 状态列从上到下全是橙色“未完成”，没运行也像出了问题 | `UiDisplay.cs:49-55` 把 PENDING、RUNNING、SKIPPED、STOPPED 和空值都映射为“未完成”；非判定步骤连 FAILED 也映射为“未完成” | 视觉噪声大；**失败信息丢失**，这一条同时是功能问题 |
| 4 | WHILE、BREAK、CONTINUE、STATEMENT、LOG 显示为问号图标 | `StepKindToIconConverter.cs` 缺少这些类型，落入 `HelpCircleOutline` | 步骤库和表格看起来像未完成的产品 |
| 5 | 字号零散，分组标题（“流程控制”“测试步骤”）只有 10px，比条目还小 | XAML 中散落 12 种字号字面量（10/11/12/13/14/15/16/17/18/19/25/27） | 层级混乱 |
| 6 | 序列列表、变量作用域使用 `▣ ↳ ▶ ● ◆ ★` 和硬编码 `#60A5FA/#34D399/#FBBF24` | `SequenceWorkspace.xaml:17-41` | 违反规则第 1、3 节；切换主题或强调色时不跟随 |
| 7 | 工具栏杂乱：带框按钮、纯文字“有未保存的修改”、蓝色链接样式的“未运行”、末尾孤立的 `⇟` 溢出符混在一行；“新建标签页/重命名/删除标签页”是三个独立按钮，不像标签页 | `SequenceWorkspace.xaml:60-80` | 主操作“运行”不突出 |
| 8 | 每个停靠面板标题都有蓝色左竖条，活动和非活动面板一样 | AvalonDock 标题样式 | 强调色被滥用，失去指向作用 |
| 9 | 右侧面板底部标签被截断成“序… 模… 运…”；两个输入框没有标签 | 右侧面板默认 20% 宽度 + 4 个标签 | 违反规则第 7 节“文字不能被裁切” |
| 10 | 网格竖线过多，分组行（初始化/主序列/清理）与数据行样式几乎一样 | `HandyControlStyles.xaml` DataGrid 样式 | 表格显得拥挤，结构不清楚 |
| 11 | 设备页内容挤在左上角的小卡片里，右侧大片空白；“输出开启”只是一个小复选框；输入框背景接近禁用色 | 设备视图 XAML | 不好看，也不安全：输出状态是最需要醒目的信息 |
| 12 | 浅色主题所有表面都是 `#FFFFFF`，复选框选中后是实心蓝方块，看不到勾 | `UiTheme.Light.xaml:3-6` | 浅色下同样没有层次 |

另外发现：`artifacts/manual-ui/device-family-voltage.png` 截到的是别的程序窗口，不是测试站。UI 验收截图需要改为按窗口句柄截取，否则截图不能作为验收证据。

## 2. 设计方向

工业测试软件需要长时间盯屏，最重要的是：状态一眼可读、主次分明、不刺眼。方向是“冷灰中性底 + 克制的强调色 + 语义色只用于状态”，参考 VS Code、JetBrains、NI TestStand 新版的深色界面。

### 2.1 深色主题令牌（建议值）

表面按 4 个层级拉开，统一带一点蓝调，边框改为半透明的同色调：

| 令牌 | 现值 | 建议值 | 用途 |
|---|---|---|---|
| WindowBrush | #30353A | #1E2227 | 窗口底、导航栏 |
| ContentBrush | #353C42 | #252A30 | 面板内容区、表格底 |
| SurfaceBrush | #3A4147 | #2C323A | 面板标题、工具栏、卡片 |
| SubtleBrush | #444B52 | #353C45 | 分组行、悬停底 |
| BorderBrush | #555555 | #3A414B | 面板边框、表格横线 |
| InputBackgroundBrush | #394147 | #1A1E23 | 输入框比内容区更深，像“凹进去” |
| SelectedRowBrush | 强调色实色 | 强调色 22% 透明叠加（蓝：#2D4A6E） | 选中行 |
| 选中行左边条 | 无 | 3px 强调色 | 指示选中，替代整行高亮 |
| TextBrush / MutedBrush | #EEEEEE / #B8B8B8 | #E6E9ED / #8C96A3 | 主文字 / 次文字 |

### 2.2 浅色主题令牌（建议值）

| 令牌 | 现值 | 建议值 |
|---|---|---|
| WindowBrush | #FFFFFF | #F3F4F6 |
| ContentBrush | #FFFFFF | #FFFFFF |
| SurfaceBrush | #FFFFFF | #F8F9FB |
| BorderBrush | — | #E2E5EA |
| SelectedRowBrush | #2563EB 实色 | #E3ECFD + 3px #2563EB 左边条 |

### 2.3 字号层级

在 `ShellStyles.xaml` 中定义 5 个 `TextBlock` 样式，页面不再写 `FontSize` 字面量：

| 样式 | 字号 / 字重 | 用途 |
|---|---|---|
| Text.Caption | 11 / Regular | 提示、计数、路径 |
| Text.Body | 13 / Regular | 正文、表格（与窗口默认一致） |
| Text.Label | 12 / SemiBold，次文字色 | 分组标题、属性标签 |
| Text.PanelTitle | 13 / SemiBold | 停靠面板标题 |
| Text.Value | 20 / SemiBold，等宽数字 | 设备测量值 |

### 2.4 状态显示

| 状态 | 显示 | 颜色 |
|---|---|---|
| 未运行（PENDING / 空） | 留空或 `—` | 次文字色 |
| 运行中 | 旋转图标 + “运行中” | 强调色 |
| 通过 / 完成 | 勾图标 + “通过”/“完成” | SuccessBrush |
| 失败 | 叉图标 + “失败” | ErrorBrush |
| 跳过 / 已停止 | “跳过”/“已停止” | 次文字色 |

失败必须显示为“失败”，不能再被并入“未完成”。

## 3. 分阶段实施

### P0：只改令牌和集中样式（约半天，风险低，效果最明显）

1. 按 2.1、2.2 更新 `UiTheme.Dark.xaml`、`UiTheme.Light.xaml`。
2. `IThemeService.cs`：`SelectedRowBrush` 改为强调色半透明叠加，新增 `SelectedRowAccentBrush` 给左边条用，选中文字保持主文字色。
3. `UiDisplay.cs:49-55`：按 2.4 拆分状态文案，FAILED 显示“失败”。
4. `StepKindToIconConverter.cs`：补齐 while（`Sync`）、break（`ExitToApp`）、continue（`SkipNext`）、statement（`CodeBraces`）、log（`TextBoxOutline`）。
5. DataGrid 样式：去掉竖线，横线用 BorderBrush，分组行用 SubtleBrush + Text.Label。

### P1：自动测试页结构（约 1–2 天）

1. 工具栏分三组：文档组（新建、保存、撤销、重做）｜运行组（运行设为主按钮，预检、暂停、停止）｜右侧状态区（运行状态胶囊 + 未保存圆点）。移除孤立的 `⇟`。
2. 序列标签页改为真正的 TabControl 样式：标签带关闭按钮，末尾 `+` 新建，右键菜单重命名，替代三个独立按钮。
3. 停靠面板标题：只有活动面板显示强调色竖条，其余用 BorderBrush。
4. 右侧面板：底部 4 个标签改为图标 + 文字并保证最小宽度不截断；序列名称和描述输入框加标签。
5. `SequenceWorkspace.xaml:17-41` 的 Unicode 图标和硬编码颜色换成 `PackIcon` + 语义画刷（主序列 / 子序列 / 局部 / 全局 / 站变量各定义一个画刷）。
6. 步骤属性区：“删除”改为次要按钮放到左侧，“应用”为主按钮放在右侧；顶部标签加选中下划线。
7. 统一字号：按 2.3 替换页面里的 `FontSize` 字面量。

### P2：设备页和手动测试（约 2 天）

1. 设备页改为“卡片网格”：每个通道一张卡片，左侧放设定值，右侧放大号测量值（Text.Value），卡片宽度随窗口伸缩。
2. “输出开启”改为醒目的开关加状态胶囊：开启时用 WarningBrush 或 ErrorBrush 底色显示“输出中”，关闭时显示灰色“已关闭”。如果库里没有开关控件，先登记到 `docs/CONTROL_LIBRARY_GAPS.md`。
3. 顶部加设备连接状态条：显示连接状态、资源地址、最近一次读取时间。
4. 手动测试平铺窗口：活动窗口只改边框颜色，标题栏不再整条变蓝；窗口高度按内容自适应，减少空白。

### P3：需要控件库配合的部分

开关控件、状态胶囊（Badge/Tag）、带关闭按钮的文档标签、数值显示卡片。如果 Industrial.Controls 1.1.0 中没有，登记到缺项清单，交由控件库发布新版本。

## 4. 验收

每一阶段完成后：深色、浅色、蓝/紫/青三种强调色各截一张图，覆盖空表格和有数据两种状态，并修好按窗口句柄截图的问题。在 `docs/CONTROL_STYLE_AUDIT.md` 中记录修改前后的对比。

## 5. 实施记录（2026-11-01，P0 + P1 第 5 项）

已修改（原文件备份在 `artifacts/ui-optimization-backup/20261101/`，双击其中的 `restore.cmd` 可整体恢复）：

| 文件 | 改动 |
|---|---|
| `Themes/UiTheme.Dark.xaml` / `UiTheme.Light.xaml` | 表面分层按 2.1/2.2 重新取值；新增 `GridLineBrush`、`MainSequenceBrush`、`SubSequenceBrush`、`LocalScopeBrush`、`GlobalScopeBrush`、`StationScopeBrush`、`ParameterScopeBrush` |
| `Services/IThemeService.cs` | 选中色从“强调色实色”改为强调色与内容底色的不透明混合（深色 24%，浅色 18%），当前步骤底色 16%/10%；选中文字使用主文字色。这推翻了原注释“选中必须是实色强调面”的决定，原因是实色遮住了行内图标和状态色 |
| `Localization/UiDisplay.cs`、`ViewModels/SequenceRow.cs` | 状态文案：判定步骤“通过/失败”，普通步骤“完成/异常”，另有“运行中/已跳过/已停止”，未运行显示“—”。颜色改为 `StatusKind` + 主题画刷，ViewModel 不再输出十六进制颜色 |
| `Themes/EditorTemplates/StepListResources.xaml` | 新增 `StepStatusTextStyle` |
| `Converters/StepKindToIconConverter.cs` | 补齐 while、break、continue、statement、log 图标 |
| `Themes/HandyControlStyles.xaml`、`SequenceWorkspace.xaml`、Hioki 视图 | 表格只保留横向分隔线，使用更浅的 `GridLineBrush` |
| `Themes/CustomTheme.xaml` | 面板标题使用 SurfaceBrush；强调色竖条只在活动面板显示 |
| `SequenceWorkspace.xaml` | 序列标签页、主/子序列、变量作用域和图例的 Unicode 字符与硬编码颜色改为 `PackIcon` + 语义画刷；步骤库分组标题 10px → 12px |
| `TestStation.Smoke/UiRegression.cs` | SurfaceBrush 期望值改为 `#F7F8FA` / `#2B3138` |

在 Linux 沙箱中已做的检查：全部 XAML 通过 XML 解析；深浅主题键集合一致；新用到的 PackIconKind 名称都在 MaterialDesignThemes 5.3.2 中存在；四种强调色 × 两种主题的选中行文字对比度最低 6.7:1（Smoke 要求 ≥ 4.5）。WPF 无法在沙箱中编译和运行，需要在 Windows 上执行 `dotnet build TestStation.slnx --no-restore` 和 Smoke `--ui` 验收。

## 6. 侧边导航（2026-11-01）

依据反编译的 Industrial.Controls 1.1.0 `NavigationView`（项目实际引用的版本）调整了原计划：

| 原计划 | 实际处理 | 原因 |
|---|---|---|
| 硬件管理移到 `FooterItems` | 留在主列表，排到最后 | 页脚是平铺按钮，不支持子项（资源/实例/家族）和选中高亮 |
| 展开 200px / 收起 56px | 保持 228 / 76 | 收起模式子项按钮 40px + 缩进 16px + 展开箭头 12px，56px 会裁掉；展开宽度缩小后长家族名会被截断 |
| 收起时补 tooltip | 无需改动 | 库已为每个图标按钮设置 `ToolTip = Title` |

已实施：一级顺序改为 自动测试 → 手动测试 → 设备测试 → 硬件管理（“视图”菜单同步）；设备家族通过可选接口 `INavigationIconSource` 提供图标（ITECH `CurrentDc`，HIOKI `SineWave`；模拟插件按 Id 推断：电子负载 `PowerPlugOutline`、万用表 `Gauge`）；导航顶部增加当前工程按钮，显示工程名、悬停显示路径，点击弹出“打开工程 / 新建工程”，收起时只保留图标。

未实施（需控件库）：导航项状态圆点、禁用态、去掉 `HideNavigationChrome`。已登记 `docs/CONTROL_LIBRARY_GAPS.md` C14。

备份：`artifacts/ui-optimization-backup/20261101-nav/`，`restore.cmd` 恢复 5 个源文件（文档未包含在脚本中）。
