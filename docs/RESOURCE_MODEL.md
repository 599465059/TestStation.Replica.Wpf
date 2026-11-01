# 设备与通信资源模型

当前版本采用统一的通信资源、设备实例和设备会话模型。TCP、串口 RS232/RS422/RS485、PCAN（以及模拟 CAN）已经通过传输层接入；VISA 只有后端接口和明确的“运行库未安装”错误，不能冒充真实连接。手动测试和自动测试从同一份工程资源目录读取设备、通道和轮询策略。

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

工程打开后，左侧“设备管理”包含“通信资源、设备实例、设备家族”三个入口。资源和实例使用草稿编辑器，保存前校验，资源参数不再由设备操作页另存一份。新建设备实例不会自动绑定或连接；实例页负责连接、断开和打开家族操作面板。连接期间资源不能修改或删除，诊断页也必须取得同一套运行时和资源租约。

## 设计边界

资源配置不引用 WPF、控件库、厂商 DLL 或具体协议实现。`ITransportSession` 只负责生命周期，字节流使用 `IByteTransportSession`，CAN 使用 `ICanBusSession`；SCPI、Modbus 和 CANopen 在其上层实现。TCP/串口/VISA 默认独占，CAN 可以共享同一物理总线；共享 CAN 的多个会话由基础设施池复用一个底层控制器，并为每个使用者保留独立接收订阅。

当前限制：VISA 厂商后端、ZLG/Kvaser/Vector 等其他 CAN 厂商会话尚未接入；PCAN 真实 DLL 的发现和会话已经准备好，实机验证仍取决于本机驱动和硬件。RS485 共享调度暂不开放，现有 SCPI 串口驱动按独占资源处理。
