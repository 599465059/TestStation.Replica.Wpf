> 当前接入记录。正式页面使用 Industrial.Controls 1.2.0；最新迁移结果及缺项以 CONTROL_LIBRARY_GAPS.md 和 CONTROL_USAGE_INVENTORY.json 为准。

# Industrial.Controls 第一轮接入

日期：2026-10-06。范围：固定版本包引用、主题适配和独立控件预览页、统一弹窗。

## 固定包与来源

- 来源：`F:\opencode\web_py\UI库\IndustrialKit\dist\packages\Industrial.Controls.1.2.0.nupkg`。
- 本工程保存位置：`vendor/nuget/Industrial.Controls.1.2.0.nupkg`。
- 原包与工程内副本 SHA-256：`FB2DCE6556E46F2183B8301157535EBC6E29896129B6E8E1B8B8FAFB648E85BE`。
- App 项目以 `Version="[1.2.0]"` 精确引用。NuGet.Config 为此包指定工程内源，其余包使用 nuget.org。
- 当前目标框架为 .NET 10，HandyControl 3.5.1、MaterialDesignThemes 5.3.2 继续使用现有版本。

只消费已发布的包。IndustrialKit 源码与 Gallery 保持只读；源码中尚未发布到此版本包的功能不会自动进入本软件。

## 分层与页面

```text
Prism ShellModule
    → ControlPreviewView + ControlPreviewViewModel
    → Industrial.Controls / HandyControl / MaterialDesign

IThemeService
    → IIndustrialControlsThemeAdapter（资源名称与对比度适配）
    → 稳定的应用画刷
    → Changed 通知 → View 中的自绘控件重绘
```

包引用和适配代码仅位于 App 层。Domain/Application 的业务模型、序列编辑器与执行引擎不依赖控件库。

预览页面及 ViewModel 通过 ShellModule 注册并由 Prism 注入。入口为顶部“视图 → 控件预览”和左侧“系统设置”下方的子入口。页面无需测试工程即可打开，点击工作区可返回工程选择页。

预览页使用独立样例数据，展示库按钮及输入样式、下拉列表、通道表格、树、RangeSlider、Dial、RadialGauge、ProgressRing 和 ColorPickerButton。卡片按可用宽度换行；控件保留最小尺寸，内容超出时使用滚动条。

## 主题接入方式

预览页局部合并包内 `Themes/Controls.xaml`，库的自定义控件仍使用包内的默认样式。正式页面的全局控件样式不被覆盖。

现有 IThemeService 保持主题权威，不调用库的全局 Theme.Initialize/Apply。以下资源由适配器映射到当前项目主题，并以稳定、可通知的画刷更新：

| 库资源 | 本项目来源 |
|---|---|
| PageBrush | WindowBrush |
| StrokeBrush | BorderBrush |
| SelectionBrush | SelectedRowBrush |
| SelectionTextBrush | TextBrush |
| DangerBrush | ErrorBrush |
| AccentTextBrush | 当前强调色经表面/选中背景对比度调整 |
| AccentForegroundBrush | 根据当前强调色选择高对比前景 |

SurfaceBrush、SubtleBrush、TextBrush、MutedBrush、AccentBrush、SuccessBrush 和 WarningBrush 直接使用原有资源。强调文字与主按钮文字的对比度检查阈值为 4.5。

当前仪表关闭连续动画，通过数据变化及 IThemeService.Changed 刷新。View 在 Loaded 时订阅主题通知，在 Unloaded 时解除订阅。切换页面时从主窗口内容宿主卸载预览页，使颜色弹窗及库控件事件正确释放；返回时保留样例 ViewModel 的值。

## 当前边界

正式页面和自动测试编辑器已使用同一套 1.2.0 资源入口；AvalonDock 继续作为停靠宿主，不复制控件库模板。

库的 FluentWindow 等组件依赖库内部 Theme.Mode/Changed。本轮保留现有主窗口；后续接入这些组件时，需要确认库提供外部主题状态和通知接口，不能通过反射修改其私有状态，也不能让两个全局主题管理器同时覆盖资源。

## 后续更新步骤

1. 控件库发布新的版本包，原版本包保留，不覆盖同版本文件。
2. 将新包加入 vendor/nuget，调整 App 的精确 PackageReference。
3. 如果资源键、依赖属性或事件契约有变化，集中适配对应接口；库版本显示从实际程序集读取。
4. 还原、编译并执行业务/UI 回归和正式 Prism 启动检查。
5. 通过后逐页迁移；需要回退时恢复原版本引用并重新编译。

```powershell
dotnet restore TestStation.slnx
dotnet build TestStation.slnx --no-restore
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src/TestStation.Smoke/Verify-PrismStartup.ps1 -ProjectPath "F:\opencode\jmytest\TestStation.Replica.Wpf\artifacts\demo-project"
```

## 验收记录

- 编译：0 警告、0 错误。
- 业务回归：原有工程读写、嵌套编辑、剪贴板、模拟执行、变量和报告通过。
- 原编辑器窗口回归：鼠标拖拽、分组、快捷键、主题、停靠及属性尺寸通过。
- 控件预览回归：浅/深 × 蓝/紫/青六组配色、旋钮双向绑定、范围滑块、启用/禁用、确认和重置命令、表格列宽、下拉列表、打开中的菜单及颜色弹窗主题通过。
- Industrial.Controls DialogHost 回归：新建协议、删除协议确认框、保存信息框和未保存修改选择框均能打开、返回结果并继续页面操作。
- 页面生命周期：离开预览页后卸载控件并关闭弹窗，返回保持样例数据，工程和序列未被预览操作修改。
- 无工程入口：预览页可以直接打开，并可返回工程选择页。
- 正式 Prism 程序：实际 EXE 的依赖注入、预览入口、确认命令、启用/禁用及标题栏主题切换通过；最大化后的序列编辑区占比仍为 65.1%，程序保持响应。
- 包文件一致性：原包与工程内副本 SHA-256 一致；包内 DLL 与实际 App 输出 DLL 的 SHA-256 均为 `8E934CC023A9BB7669B5B73100B608198E8F7BD630FBC88481A4A4A6A8B3ACE0`，确认实际加载最新发布控件。

控件截图：`artifacts/ui/controls-preview-light.png`、`controls-preview-dark.png`。
正式程序检查另输出 `artifacts/ui/prism-controls-light.png`、`prism-controls-dark.png`。

