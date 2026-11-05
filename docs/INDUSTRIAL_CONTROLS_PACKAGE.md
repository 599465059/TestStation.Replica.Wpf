# Industrial.Controls 包基线

- 包：`Industrial.Controls 1.1.0`
- 来源：`F:\opencode\web_py\UI库\IndustrialKit\dist\packages\Industrial.Controls.1.1.0.nupkg`
- 本项目副本：`vendor/nuget/Industrial.Controls.1.1.0.nupkg`
- SHA-256：`256C88D61791DD7B6F1765612FFD7948F53AFF7C7BA0057E4D03B5A5FDBB1E04`
- 更新日期：2026-10-07

当前工程固定使用 `1.1.0` 包；项目固定使用上述文件，不修改 IndustrialKit 源码。以后控件库发布新内容时，应发布递增包版本，或先更新本文件和包哈希后再接入。

本次手动测试接入 1.1.0 中除配置/通道管理外的全部工业控件：串口数据显示、数字示波器、滑条指令、开关、模式选择、数值显示、实时数值显示、实时数值输入、仪表盘、状态指示灯、进度条、命令按钮、触发按钮、范围滑块、进度环和折叠容器。它们通过页面层的 `MemoryBackend` 提供模拟数据和命令回传，未连接真实硬件。
