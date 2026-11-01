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
| `IDeviceFamilyCatalog` | 已安装家族元数据和启用状态边界 | `ManualPluginViewRegistry` 组合根适配；外部扫描器暂未接入 |
| `IDeviceFamilyModule` | 家族型号目录和驱动工厂的 WPF 无关契约 | `ItechPowerSupplyFamilyModule`；首期应用内注册，外部插件包暂未接入 |
| `IDeviceInstanceService` | 工程设备实例、资源绑定和会话生命周期边界 | ITECH 具体实现由 `PowerSupplyDeviceService` 提供；PW6001 由 `HiokiPowerAnalyzerDeviceService` 提供 |
| `IResourceConfigurationService` | 资源草稿校验、原子保存、删除和租约检查 | `ResourceConfigurationService` |
| `IDeviceManagementService` | 聚合设备家族实例列表、连接状态和批量断开 | `DeviceManagementService` |
| `ITransportDiscoveryService` | 串口、PCAN 和 VISA 后端可用性发现 | `TransportDiscoveryService` |
| `ITransportDiagnosticService` | 使用正式传输工厂进行原始字节/CAN 诊断 | `TransportDiagnosticService` |
| `IByteTransportSession` | TCP、串口和 VISA 字节交换 | `TcpTransportSession`、`SerialTransportSession`、`VisaTransportSession` |
| `ICanBusSession` | CAN/CAN FD 帧发送、持续接收和过滤 | `PcanCanBusSession`、`SimulatedCanBusSession` |

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
| `IManualPluginViewProvider` | 提供一个设备插件的手动测试界面及版本信息 | `ManualTestView` 与 `DeviceTestingView` 宿主；外部插件包尚未接入 |
| `IManualPluginViewRegistry` | 注册、移除和通知手动插件界面 | `ManualPluginViewRegistry` |

## 通信和设备接口

这些接口已经在 Application 层定义。ITECH 设备已由 `PowerSupplyDeviceService` 贯通 TCP/串口传输、SCPI 会话、驱动和设备会话；HIOKI PW6001 已由 `HiokiPowerAnalyzerDeviceService` 贯通 TCP、SCPI 和只读测量通道。PCAN-Basic 已通过 Infrastructure 适配器接入，并完成四通道设备的真实通道 1→2 收发和 OpenTAP 短验收；其他 VISA、ZLG、NI、Kvaser 等厂商后端和外部插件包仍未接入；通用 VISA/CAN 模拟会话继续保留。

| 接口 | 建议职责 | 关键要求 |
|---|---|---|
| `ITransportSession` | 打开/关闭通信资源、报告状态和资源标识 | 只负责生命周期；不包含字节交换、SCPI、DBC 或设备业务语义 |
| `IByteTransportSession` | TCP、串口和 VISA 的字节写入及请求/响应 | `TcpTransportSession`、`SerialTransportSession`、`VisaTransportSession` |
| `ICanBusSession` | CAN/CAN FD 帧发送、持续接收、过滤和取消 | `SimulatedCanBusSession`、`PcanCanBusSession`；厂商适配器不进入 Application |
| `ICanVendorAdapter` | 厂商无关的通道枚举、句柄规范化和控制器创建端口 | Application 契约；当前由 `PcanVendorAdapter` 实现 |
| `ICanHardwareDiscovery` | 只读枚举厂商通道和能力，不打开或占用设备 | 当前由 `PcanVendorAdapter` 实现 |
| `ITransportSessionFactory` | 根据工程资源创建传输会话 | `TransportSessionFactory`；`modbus_tcp`/`modbus_rtu` 仅映射到底层 TCP/串口 |
| `IByteProtocolSession` | 二进制协议请求/响应边界 | 为 Modbus 等协议预留，当前未实现协议编解码 |
| `ICanProtocolSession` | 面向 CAN 帧的协议会话边界 | 为 CANopen/J1939 预留，当前未实现协议编解码 |
| `IDeviceDriver` | 将设备命令映射为读写通道和设备状态 | `ItechPowerSupplyDriver`、`HiokiPowerAnalyzerDriver`，不直接依赖页面 |
| `IDeviceDriverFactory` | 根据设备类型、厂商和版本创建驱动 | `ItechPowerSupplyDriverFactory`；旧模拟工厂仍供历史模拟通道使用 |
| `IProtocolSession` | SCPI 字符串发送、查询、终止符和通信记录 | `ScpiProtocolSession`；只负责协议，不管理设备生命周期 |
| `IDeviceSession` | 单设备会话的连接、断开和通道读写 | `ItechPowerSupplyDeviceSession`、`HiokiPowerAnalyzerDeviceSession` |
| `IChannelRuntimeCoordinator` | 手动订阅轮询、单次读写和自动运行租约 | `SimulatedChannelRuntimeCoordinator` + `CompositeDeviceIoBackend`，自动期间暂停手动访问 |
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
3. 再实现串口、TCP、CAN、Modbus 适配器及厂商 DLL 包装；PCAN-Basic 已完成第一套厂商适配器。
4. 实现插件注册和打包接口，把设备配置、驱动、自动测试步骤和手动界面纳入插件系统。手动界面宿主已具备标签查看/平铺查看能力，但当前仍需插件目录扫描和程序集加载器。

接口完成后，必须为每个实现增加 Smoke 或单元测试；硬件测试使用可替换的 `ITransport`，不把设备依赖写入 UI 回归。

通用传输层短验收入口：

```powershell
dotnet run --project tools/TestStation.TransportAcceptance/TestStation.TransportAcceptance.csproj
```

该入口只验证 TCP 回环、串口参数校验、CAN 模拟共享总线/过滤、VISA 模拟后端和缺失后端错误，不连接真实硬件，也不运行长序列。
