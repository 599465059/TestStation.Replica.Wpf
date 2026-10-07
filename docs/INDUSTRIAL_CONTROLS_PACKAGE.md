# Industrial.Controls 包基线

- 包：`Industrial.Controls 1.2.0`
- 来源：`F:\opencode\web_py\UI库\IndustrialKit\dist\packages\Industrial.Controls.1.2.0.nupkg`
- 本项目副本：`vendor/nuget/Industrial.Controls.1.2.0.nupkg`
- SHA-256：`F67E67CEF259FA018D37B116DA11B885595AD92F27EF18906DF2B7655F5A1737`
- 更新日期：2026-10-06

当前发布目录仍使用 `1.2.0` 版本号，但包内容已更新；项目固定使用上述文件，不修改 IndustrialKit 源码。以后控件库发布新内容时，应发布递增包版本，或先更新本文件和包哈希后再接入。

本次手动测试接入的控件包括：串口数据显示、实时数值显示、实时数值输入、命令按钮、仪表盘和状态指示灯。它们通过页面层的 `MemoryBackend` 提供模拟数据和命令回传，未连接真实硬件。
