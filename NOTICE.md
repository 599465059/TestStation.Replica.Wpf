# 参考来源与组件说明

本轮仅修改 `TestStation.Replica.Wpf`。参考工程 `F:\opencode\web_py\test` 和原 Python 工程保持原样。

参考工程名称：TestStand Editor。其 LICENSE 声明：`Copyright (c) 2026 比怪兽还怪. All rights reserved.`，原许可为 TestStand Editor Trial License。本次按用户确认拥有的代码迁移授权，适配其缩进思路、步骤图标映射、表格模板组织、控件状态以及拖拽/文档编辑交互。原许可原文保留在 `docs/reference-TestStandEditor-LICENSE.txt`。此说明不授予新的再分发、转授权或销售权利。

没有迁移参考工程的主窗口、正式文档模型、执行引擎或插件协议。当前工程仍以自己的 `SequenceDefinition`、`SequenceEditor` 和执行引擎为业务基础。

本工程使用的第三方包：

| 包 | 版本 | 用途 |
|---|---|---|
| Prism.DryIoc | 9.0.537 | 模块及依赖注入 |
| HandyControl | 3.5.1 | 基础控件样式和窗口组件 |
| Industrial.Controls | 1.1.0 | 用户提供的工业控件库、主题资源与弹窗样式 |
| MaterialDesignThemes | 5.3.2 | PackIcon 图标 |
| Dirkster.AvalonDock | 4.72.0 | 停靠面板 |
| CommunityToolkit.Mvvm | 8.4.0 | 属性通知及命令 |

第三方组件仍按各自 NuGet 包中的许可使用；本次适配不改变这些许可。

Industrial.Controls 使用用户提供的最新已发布包 `IndustrialKit/dist/packages/Industrial.Controls.1.1.0.nupkg`，原包副本保存于本工程 `vendor/nuget`。来源工程仅只读检查，未修改或复制 Gallery 应用；包内的 README、第三方说明和许可文件随原包保留。
