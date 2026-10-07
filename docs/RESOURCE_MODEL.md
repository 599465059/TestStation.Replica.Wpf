# 设备与通信资源模型

当前版本先建立统一配置模型，暂不打开真实 CAN、串口或 TCP。手动测试和自动测试后续都从同一份资源目录读取设备、通道和轮询策略。

## 文件位置

```text
工程目录/
└── resources/
    └── resources.json
```

文件采用包装格式：

```json
{
  "format": "test-station-resources",
  "version": 1,
  "resources": {
    "transports": [],
    "devices": []
  }
}
```

## 对象关系

```text
TransportResource
        ↑ transport_id
DeviceDefinition ──→ ChannelDefinition
                         ├── ChannelMapping
                         └── PollingPolicy
```

- `TransportResource` 只描述通信资源及其参数，例如串口号、波特率、TCP 地址或 CAN 通道。
- `DeviceDefinition` 描述设备、驱动 ID、协议 ID 和所属通信资源。
- `ChannelDefinition` 是手动测试和自动测试共同使用的通道单位，包含数据类型、单位、读写权限、读写帧字段映射和轮询策略。
- `ChannelMapping` 只保存协议映射和命令模板，不负责实际发送。
- `PollingPolicy` 支持 `manual`、`periodic`、`event` 三种模式，周期、超时、重试和事件标识都属于通道配置。

## 当前代码入口

| 层 | 文件 | 责任 |
|---|---|---|
| Domain | `src/TestStation.Domain/Resources/ResourceCatalog.cs` | 资源、设备、通道和值对象 |
| Application | `src/TestStation.Application/Resources/ResourceServices.cs` | 仓储接口、目录服务和配置校验 |
| Infrastructure | `src/TestStation.Infrastructure/Resources/ResourceCatalogRepository.cs` | `resources.json` 的读写和临时文件替换 |
| App | `src/TestStation.App/Modules/ResourceModule.cs` | Prism 注册资源仓储和服务 |
| Smoke | `src/TestStation.Smoke/ResourceRegression.cs` | 保存、重新加载、设置恢复和非法配置检查 |

工程打开后，左侧“设备配置”进入资源管理页面。页面采用三栏布局：通信资源、设备、通道与轮询；顶部可新建、删除、刷新和保存。编辑表格中的名称、驱动、通信资源、读写权限、轮询模式和周期后，点击“保存”才写入工程文件。删除设备前会由服务校验通信资源引用，避免留下悬空配置。

## 设计边界

资源配置不引用 WPF、控件库、厂商 DLL 或具体通信类。真正的通信将在后续由 `ITransport` 和 `ITransportFactory` 实现，设备驱动通过 `IDeviceDriver` 读取资源模型并提供业务通道。这样替换厂商 DLL、增加 CAN DBC 或修改轮询周期时，不需要修改画布控件和自动测试编辑器。

下一步建立内存版 `ITransport`、`IDeviceDriver` 和 `IChannelService`，用模拟设备验证手动画布读写、周期轮询和自动测试步骤；真实硬件适配器在模拟契约稳定后再加入。
