# 控件迁移与控件库缺项

基线：已发布 Industrial.Controls 1.2.0；只消费原包。IndustrialKit 源码、Gallery 和原 Python 工程均未修改。

## 已接入

正式页面统一使用包中的 Button、TextBox、ComboBox/ComboBoxItem、ListBox/ListBoxItem、TreeView/TreeViewItem、DataGrid/DataGridRow/DataGridCell、ToolTip 样式。动态创建的基础控件采用相同隐式样式。SearchBar、NumericUpDown、PasswordBox 等库中已定义的类型也通过全局字典接入。

ResourceBootstrap 顺序加载 HandyControl 依赖、Industrial.Controls 原始字典、项目主题和布局样式；直接捕获库样式对象，避免跨字典的隐式类型键被项目同名样式覆盖。应用不复制任何基础 ControlTemplate。表格列默认允许调整，网格线和文本对齐集中配置。

## 待用户完善的能力

手动画布测试阶段（用户已授权）：现有 DesignerWorkspace 的内置工具栏、固定范围和仪表默认尺寸不作为正式页面基础。应用实现专用布局交互层，并临时组合 Border/TextBlock 显示“名称 / 数值 / 单位”，不复制基础模板。待库提供可独立承载的画布、紧凑数值视图、手势事件和宿主文档/命令接口后替换测试视图。IndustrialKit 源码不修改。

| 编号 | 位置/类型 | 缺少的能力 | 当前处理 | 建议库接口 |
|---|---|---|---|---|
| C01 | 标题菜单、右键菜单、导航弹出菜单 / Menu、MenuItem、ContextMenu | 1.2.0 已有 `KitMenu`、`KitMenuItem`、`KitContextMenu` 配色/状态契约，未提供专属模板及 MenuItem.Role 的模板选择 | 直接继承 Kit 样式，ResourceBootstrap 复用 HandyControl 的普通项/顶层/子菜单模板对象；不在页面复制模板；映射 MenuBrush/MenuHoverBrush | 发布含顶层、子菜单、普通项角色的完整菜单模板 |
| C02 | 序列 ENABLED / CheckBox | 库专属勾选、未勾选、三态、禁用样式 | 保留统一 HandyControl 样式，主题同步 | 发布可调尺寸 CheckBox 样式 |
| C03 | 表格表头 / DataGridColumnHeader | 库字典未单独封装表头、排序、调整列宽状态 | 保留 HandyControl 表头模板，集中动态配色 | 发布 KitDataGridColumnHeader 样式 |
| C04 | 全部滚动区域 / ScrollBar | 库专属横纵滚动条及各交互状态 | 保留 HandyControl 滚动条 | 发布 ScrollBar 样式与尺寸资源 |
| C05 | 步骤属性 / Expander | 基础 Expander 专属模板缺项；AnimatedExpander 不能无条件等价替换现有绑定 | 保留 HandyControl Expander | 标准 Header/Content/IsExpanded 兼容和主题资源 |
| C06 | 菜单分隔符 / Separator | 库专属分隔符资源 | 保留 HandyControl 分隔符 | 统一方向、颜色、厚度资源 |
| C07 | 主窗口、工程命名窗口 | FluentWindow/DialogWindow 的现成标题栏、尺寸和按钮不能保持现有壳；库静态 Theme.Mode 与本项目主题未提供外部同步入口 | 保留现有壳；其中基础输入和按钮已替换 | 外部主题/调色板入口，可配置标题栏和对话框最小尺寸，MaterialDesign 图标 |
| C08 | InfoBar、Timeline 等缓存画刷的扩展控件 | Theme.Apply 会覆盖外部调色板且提前通知；资源覆盖后缓存内容未必同步 | 正式页面不用不兼容组件；预览中自绘控件在本项目主题事件上重绘 | 外部调色板提供者及完整更新后统一通知 |
| C09 | 紧凑表格行、按钮圆角 | 表格行仍继承 HandyControl 内部模板；按钮模板圆角固定，公开属性不能完全控制所有内部间距 | 公开属性调整行高、边框、Margin；无法调整的内部细节保留 | 紧凑 DataGridRow 模板/间距资源，按钮圆角资源 |
| C10 | 文件/文件夹选择窗口 | 库使用系统文件对话框，无法按项目主题绘制操作系统窗口 | 使用系统文件选择器 | 如需要完全统一，提供可选库内文件浏览器 |
| C11 | ToolBar/ToolBarTray 与未来使用的 RadioButton/ToggleButton、ProgressBar、Slider、Calendar 等 | 1.2.0 已提供 `KitToolBar` 样式契约，保留 WPF 模板的溢出按钮未使用库动态画刷；ToolBarTray 暂无独立资源键 | 自动测试与主窗口工具栏使用 KitToolBar；公共 ToolBarPalette 仅为原溢出按钮绑定项目画刷，不复制模板；ToolBarTray 作为标准工具栏布局容器；其他未使用类型不额外引入 | 工具栏全部模板部件的主题契约和 ToolBarTray 样式 |
| C12 | 主窗口底部 / StatusBar | 未封装 StatusBar 的库专属模板 | 保留 WPF 状态栏类型，使用项目动态背景/文字；明确作为缺项 | 发布 StatusBar 和 StatusBarItem 样式 |
| C13 | 协议三个页面 / GridSplitter、ScrollViewer | 库未封装专用分割器与滚动容器样式 | 分割器使用 WPF 类型和动态边框色；滚动容器沿用 HandyControl 依赖样式、库缺少的滚动条回退见 C04 | 发布 GridSplitter/ScrollViewer 样式和命中区域尺寸资源 |

AvalonDock 为用户指定保留的停靠系统；Grid/Border/StackPanel 等为布局元素；协议字节画布和插入线为业务渲染。这些不属于基础控件迁移缺项。

不以“WPF 类名”判断是否使用库：必须检查实际样式/模板来源。当前有明确记录的回退，不能宣称全部基础控件已经由 Industrial.Controls 独立封装。

