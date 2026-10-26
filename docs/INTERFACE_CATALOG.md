# Test Station 接口清单

本文档区分“当前已实现接口”和“后续计划接口”。接口名称是代码契约，显示文字和协议字段不能替代接口标识。

## 当前已实现

### Application 层

| 接口 | 责任 | 当前实现 |
|---|---|---|
| `ISequenceEditingService` | 插入、移动、删除、复制、剪切、粘贴和属性变更的应用入口 | `SequenceEditingService` |
| `ISequenceDocumentService` | 文档会话、保存基线、事务、撤销重做和复制 | `SequenceDocumentService` |
| `IChannelGateway` | 执行引擎读取、写入和快照通道值 | `SimulatedChannelGateway`；真实网关待接入 |
| `IManualCanvasEditingService` | 网格吸附、移动/缩放预览和画布校验 | `ManualCanvasEditingService` |
| `IManualCanvasDocumentService` | 手动画布控件新增、边界更新、属性替换和批量删除 | `ManualCanvasDocumentService` |
| `IManualCanvasRepository` | 手动画布文件读取和保存 | `ManualCanvasRepository` |
| `IProtocolRepository` | `.tsproto` 读取、保存、导入、导出和删除 | `ProtocolRepository` |
| `IProtocolLibraryService` | 协议库生命周期和错误集合 | `ProtocolLibraryService` |
| `IProtocolEditingService` | 帧/字段编辑、移动、重叠校验和保留区填充 | `ProtocolEditingService` |
| `IDbcService` | DBC 文件枚举、导入和解析 | `DbcService` |
| `IProtocolPreviewService` | 自定义帧离线生成和字段预览 | `ProtocolPreviewService` |
| `IResourceCatalogEditingService` | 资源、设备、通道的结构编辑、引用检查和属性更新 | `ResourceCatalogEditingService` |
| `IResourceCatalogService` | 资源目录打开、保存、验证和变更通知 | `ResourceCatalogService` |
| `TransportSettingsAccessor` | 按串口、TCP、CAN、Modbus 类型读取、校验和写入通信参数 | `TransportSettingsAccessor` |

### App 层

| 接口 | 责任 | 约束 |
|---|---|---|
| `IAutoTestDisplayService` | 从 `SequenceDefinition` 生成显示行、缩进和分支行 | 只返回显示适配数据，不修改文档 |
| `ISequenceDropTargetService` | 将鼠标位置解析成合法 `SequenceInsertTarget` | 只计算落点，不执行移动 |
| `ISequenceInteractionService` | WPF 命中测试、拖拽、键盘和右键交互 | 不直接写领域模型 |
| `IThemeService` | 深浅主题和强调色切换 | 通过动态资源通知 View |
| `IIndustrialControlsThemeAdapter` | 将应用语义资源映射到 Industrial.Controls | 不调用库的全局主题覆盖 |
| `ISequenceDialogService` | 序列未保存、删除和切换确认 | 使用发布控件库弹窗 |
| `IProtocolDialogService` | 协议未保存、删除和导入提示 | 使用发布控件库弹窗 |
| `IManualPluginViewProvider` | 提供一个设备插件的手动测试界面及版本信息 | `ManualTestView` 宿主；插件加载器尚未接入 |
| `IManualPluginViewRegistry` | 注册、移除和通知手动插件界面 | `ManualPluginViewRegistry` |

## 通信和设备接口

这些接口已经在 Application 层定义，并提供内存传输、模拟设备和模拟轮询实现。真实硬件仍由后续 `TestStation.Drivers` 或插件实现。

| 接口 | 建议职责 | 关键要求 |
|---|---|---|
| `ITransport` | 打开/关闭通信资源、发送原始字节、接收数据和连接状态 | 已有 `MemoryTransport`；不包含 SCPI、DBC 或设备业务语义 |
| `ITransportFactory` | 根据资源配置创建 CAN、串口、TCP、Modbus 传输 | 已有 `SimulatedTransportFactory`；真实适配器待接入 |
| `IDeviceDriver` | 将设备命令映射为读写通道和设备状态 | 已有 `SimulatedDeviceDriver`，不直接依赖页面 |
| `IDeviceDriverFactory` | 根据设备类型、厂商和版本创建驱动 | 已有 `SimulatedDeviceDriverFactory` |
| `IProtocolCodec` | SCPI、自定义协议、DBC、Modbus 帧编码/解码 | 已有 `PassthroughProtocolCodec` 契约实现；具体协议编解码待接入 |
| `IChannelService` | 通道读写、缓存、质量状态和更新时间 | 已有 `SimulatedChannelService`，手动测试与自动测试共用 |
| `IPollingService` | 周期询问、事件触发、超时、重试和取消 | 已有周期轮询及 Start/Stop/Restart 生命周期；事件订阅待补充 |
| `IEventSubscriptionService` | DBC 事件报文、接收过滤和订阅 | 支持多个订阅者和生命周期取消 |
| `IDriverCatalog` | 查看内置驱动、版本、协议和能力说明 | 已有 `BuiltInDriverCatalog`，当前提供模拟驱动说明 |

## 计划新增的插件接口

| 接口 | 责任 |
|---|---|
| `IPluginManifest` | 插件 ID、名称、版本、宿主版本、依赖和能力 |
| `IPlugin` | 插件加载、注册和释放生命周期 |
| `IPluginRegistrar` | 注册传输、设备驱动、协议编解码器、步骤和控件 |
| `IPluginPackageService` | 将设备配置、指令、协议、资源映射打包为插件包 |
| `IPluginCatalog` | 导入、启用、禁用、升级和卸载插件 |

插件只能通过注册接口扩展能力，不能反射修改宿主 ViewModel，也不能覆盖宿主主题资源。插件包必须包含清单、版本、依赖、配置和迁移信息。

## 接口落地顺序

1. ~~定义 `ITransport`、`ITransportFactory`、`IProtocolCodec` 和 `IChannelService` 的领域无关契约。~~ 已完成第一版契约，并提供模拟工厂、透传编解码和驱动目录。
2. ~~用内存实现和模拟设备实现自动测试，保证执行引擎不依赖真实硬件。~~ 已完成内存传输、模拟设备、读写、事件和周期轮询 Smoke 验收。
3. 再实现串口、TCP、CAN、Modbus 适配器及厂商 DLL 包装。
4. 实现插件注册和打包接口，把设备配置、驱动、自动测试步骤和手动界面纳入插件系统。手动界面宿主已具备标签查看/平铺查看能力，但当前仍需插件目录扫描和程序集加载器。

接口完成后，必须为每个实现增加 Smoke 或单元测试；硬件测试使用可替换的 `ITransport`，不把设备依赖写入 UI 回归。
