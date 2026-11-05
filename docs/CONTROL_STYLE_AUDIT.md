> 当前检查记录。正式页面使用 Industrial.Controls 1.1.0；库没有覆盖的类型和回退范围以 CONTROL_LIBRARY_GAPS.md 和 CONTROL_USAGE_INVENTORY.json 为准。

# 控件来源与主题检查

日期：2026-10-07。检查范围为本工程 `src/TestStation.App` 的页面 XAML、集中样式、代码创建的控件和实际窗口。参考工程与 IndustrialKit 源码未修改。

## 当前使用范围

**正式页面已经切换到 Industrial.Controls 1.1.0 的统一资源入口。** 能由发布包提供模板的基础控件直接使用其样式；发布包尚未覆盖的类型保留集中适配的 HandyControl 回退，并在缺项清单中登记。自动测试、工程选择、导航和工具栏不再使用默认 WPF 外观。

| 控件或区域 | 实际来源与入口 |
|---|---|
| 按钮、文本框、下拉框 | Industrial.Controls 原包模板及集中主题适配，`ResourceBootstrap.cs` / `HandyControlStyles.xaml` |
| 复选框 | 已登记的 HandyControl 模板回退 |
| 表格及单元格、行、列头 | HandyControl `DataGrid` 和命名子控件样式，文本列共用 `AppDataGridTextStyle` |
| 列表、树及选中项 | Industrial.Controls 模板；活动/非活动选择接入本工程稳定主题画刷 |
| 菜单和右键菜单 | KitMenu/KitMenuItem/KitContextMenu 样式，集中复用 HandyControl 角色模板；MenuBrush/MenuHoverBrush 映射本工程主题 |
| 工程和序列工具栏 | KitToolBar；紧凑按钮继承 KitButton；原溢出按钮仅绑定主题画刷，未复制模板 |
| 提示、折叠区、滚动区域、滚动条 | 提示为库样式；其他按缺项登记保留回退 |
| 步骤行显示 | 本工程 DataTemplate 组合 MaterialDesign PackIcon 与文本；业务行模板不是基础控件模板 |
| 控件预览的基础控件、仪表、旋钮、颜色选择等 | Industrial.Controls 1.1.0，样式字典在预览页局部加载 |
| 停靠面板、浮动窗、标题按钮、分割线 | 保留 AvalonDock 和 `CustomTheme.xaml` 的现有模板；这部分仍有项目停靠模板，属于已确认的保留范围 |
| 主窗口标题栏与导航布局 | 保留现有 WindowChrome 外壳，按钮使用 Industrial.Controls 样式；页面搜索已移除 |
| 新建工程名称弹窗 | Industrial.Controls DialogHost/DialogService |
| 文件夹选择 | Windows OpenFolderDialog 系统窗口，属于系统界面 |

Grid、StackPanel、Border、Canvas 等是 WPF 布局和绘图元素，TextBlock 负责文本显示。基础控件如 ScrollViewer、Button、DataGrid 也保留 WPF 类型，由库的样式绘制；不能仅凭类型名称判断是否使用控件库。

## 本次发现与修复

1. 插入线起点原来使用 `4 + Depth * 22`，嵌套越深缺口越大。现统一为表格完整宽度；插入目标、合法性检查和嵌套规则保持原业务判断。
2. 文本列的 `DataGridTextColumn.DefaultElementStyle` 优先于隐式 TextBlock 样式，导致文字偏上。正式表格的文本列现在显式使用集中居中样式，并保留省略显示与可调列宽。
3. HandyControl 的表格行、单元格、列头和滚动条使用命名样式。此前按类型查找会回退到 WPF 系统样式，已改为明确引用 `DataGridRowStyle`、`DataGridCellStyle`、`DataGridColumnHeaderStyle` 和 `ScrollBarBaseStyle`。
4. 普通按钮悬停依赖 `SecondaryRegionBrush`，之前没有完整映射到项目主题。现同步悬停、按下、禁用、勾选边框和图标文字画刷；工具栏组合文字跟随按钮 Foreground。
5. 下拉选项补充集中 `AppComboBoxItemStyle`，继承 HandyControl 的 `ComboBoxItemBaseStyle`，并接入选中、悬停、禁用颜色。
6. 未勾选复选框原来沿用库中的白色默认背景，现改用 `InputBackgroundBrush` / `InputBorderBrush`；勾选后继续使用强调色。

## 验证

- 编译、业务 Smoke、实际 WPF 窗口 UI Smoke、实际 Prism 程序启动。
- 库样式检查对比真实 HandyControl 资源对象，不能仅以“存在 ControlTemplate”判断，因为 WPF 默认样式也有模板。
- 实际鼠标拖入 THEN/ELSE 时检查插入线两端，并保存拖拽过程截图。
- 通过布局边界检查 `#`、`TYPE`、`SETTINGS`、`STATUS` 非空文字的垂直中心。
- 反复切换深浅主题，实际鼠标悬停“预检”按钮，检查模板内部背景、禁用文字及组合文字。
- 在两种主题下取消并恢复步骤勾选，检查未勾选框的实际模板背景。
- 保留原有嵌套、跨分组拖拽、快捷键、折叠、停靠恢复及预览页六组主题/强调色测试。

本次执行结果：编译 0 警告、0 错误；业务 Smoke、UI Smoke 和正式 Prism 程序检查全部通过。UI Smoke 确认 20 类基础控件的实际库样式来源，以及 14 个非空文本单元格的垂直中心；正式程序最大化后序列编辑区域占内容宽度 65.1%。

截图位于 `artifacts/ui/editor-insertion-then.png`、`editor-insertion-else.png`、`editor-hover-light.png` 和 `editor-hover-dark.png`。

后续控件库发布新版本时，应逐页确认资源契约后更新固定包引用，保持主题适配边界，不能复制控件库源码或一次性全局覆盖当前样式字典。

