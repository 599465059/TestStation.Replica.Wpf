# .NET 10 升级与验证记录

日期：2026-10-05。范围：`F:\opencode\jmytest\TestStation.Replica.Wpf`。

## 目标框架与 SDK

| 项目 | 升级后的目标框架 |
|---|---|
| TestStation.Domain | `net10.0` |
| TestStation.Application | `net10.0` |
| TestStation.App | `net10.0-windows` |
| TestStation.Smoke | `net10.0-windows` |

工程根目录新增 `global.json`：SDK 基线为 `10.0.200`，使用 `latestFeature` 滚动策略并禁止预览版。允许使用同一 .NET 10 主版本内更新的补丁及功能带。

验证机器使用 SDK `10.0.202`，已安装 .NET Runtime 和 Windows Desktop Runtime `10.0.6`。编译产物的 runtimeconfig 声明使用 .NET 10 Core/Desktop Runtime。

领域层和应用层继续不依赖 WPF。现有 Prism、HandyControl、MaterialDesign、AvalonDock 和 MVVM Toolkit 的包版本保持原值。现有工程文件格式、序列模型和执行机制保持兼容。

## 兼容调整

新版 WPF 同时提供 `System.Windows.ThemeMode` 和 `Window.ThemeMode`，与项目原有 `TestStation.App.Services.ThemeMode` 重名。

主窗口和 UI 回归代码通过 `AppThemeMode` 别名明确引用项目主题枚举。深浅主题及强调色仍交给现有 `IThemeService` 管理；没有启用 WPF 的实验性主题接口，也没有屏蔽其编译诊断。

README 启动路径和 `Verify-PrismStartup.ps1` 已切换至 `bin/Debug/net10.0-windows`。

## 验证

```powershell
dotnet restore TestStation.slnx
dotnet build TestStation.slnx --no-restore
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj
dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui
powershell.exe -NoProfile -ExecutionPolicy Bypass -File src/TestStation.Smoke/Verify-PrismStartup.ps1 -ProjectPath "F:\opencode\jmytest\TestStation.Replica.Wpf\artifacts\demo-project"
```

| 检查 | 结果 |
|---|---|
| NuGet 还原及整个解决方案编译 | 通过，0 警告、0 错误 |
| 业务回归 | 工程和序列读写、嵌套编辑、剪贴板、变量、模拟执行、运行报告通过 |
| UI 回归 | 19 类基础控件样式、实际鼠标拖拽、跨分组移动、折叠、双击插入和键盘快捷键通过 |
| 主题与布局 | ENABLED 反复切换、深浅主题及强调色、右键菜单、停靠恢复、属性滚动和最小尺寸通过 |
| 正式 Prism 程序 | 实际启动、打开示例工程、导航、最大化、主题及强调色按钮通过，程序保持响应 |
| 最大化序列区 | 实测占停靠内容宽度约 65.1% |

更新后的截图保存在 `artifacts/ui/editor-light.png`、`editor-dark.png` 和 `prism-maximized.png`。

本次只升级现有软件。`IndustrialKit` 与其他参考、原始工程未修改，也未接入新的控件库。
