# 设备家族与通信架构

## 分层

```text
WPF View / ViewModel
        ↓
设备服务（Application）
        ↓
设备会话与驱动（Infrastructure）
        ↓
协议会话（SCPI / Modbus / CAN）
        ↓
通信传输（TCP / Serial / VISA / CAN）
```

Domain 只保存设备、型号、通道和能力模型。Application 定义设备访问、会话、资源租约和协议边界。Infrastructure 实现具体通信和设备驱动。App 只负责页面、ViewModel、导航和配置编辑。

## 资源和设备

`TransportResource` 描述电脑上的物理或网络资源，包含串口参数、TCP 地址、VISA 资源名、CAN 总线参数和占用模式。`DeviceDefinition` 描述一个设备实例，引用通信资源，并保存设备家族、型号、协议和 RS485/CAN 地址。

独占资源适用于 RS232、单设备 TCP 和独占 VISA 会话。RS232、RS422、RS485 是同一串口传输的线路模式，不是协议；RS485 的站号放在设备或协议配置。共享总线资源适用于 CAN；多个设备实例可以共享同一 CAN 通道，但每个设备必须配置独立地址或节点 ID，访问由设备或协议层协调。

通用传输契约位于 `Application/Hardware`：`ITransportSession` 只管理生命周期和状态，字节流由 `IByteTransportSession` 提供，CAN/CAN FD 由 `ICanBusSession` 提供。TCP、串口和 VISA 都使用字节流接口；CAN 保持帧模型，不能伪装成字符串或普通字节请求/响应。`TransportResource` 只保存传输类型和传输参数，Modbus、SCPI、CANopen 等协议字段不写入传输层。

## 艾德克斯电源

电源家族由三份数据组成：家族 Profile（型号、能力、通道和 SCPI 映射，保存在本机版本化文件）、工程设备实例（实例名称、型号和资源 ID，保存在工程资源目录）以及通信资源（TCP/串口参数，保存在工程资源目录）。前台操作页面和后台四类配置页面使用同一份 Application 服务；型号覆盖只允许一层完整通道覆盖，未覆盖时继承家族公共定义。

前台可以管理多个电源实例。每个实例通过 `PowerSupplyDeviceService` 创建独立的 `ITransportSession` → `ScpiProtocolSession` → `ItechPowerSupplyDriver` → `ItechPowerSupplyDeviceSession` 链路；后台保存会检查已连接实例并要求断开。ITECH 页面首期仍只开放 TCP/串口；VISA/CAN 的通用传输能力独立提供，设备家族接入时再声明是否支持。

当前公共 SCPI Profile 位于 `Application/Devices/ItechPowerSupply`，驱动位于 `Infrastructure/Devices/ItechPowerSupply`。默认通道使用公共 SCPI 命令：

```text
SOUR:VOLT {value} / SOUR:VOLT?
SOUR:CURR {value} / SOUR:CURR?
OUTP ON|OFF / OUTP?
MEAS:VOLT? / MEAS:CURR?
SYST:ERR?
```

TCP 和串口复用同一个 SCPI 协议会话。VISA 当前提供 `VisaTransportSession`、不可用后端和显式注入的模拟后端；缺少真实 VISA 运行库时会明确报“后端未安装”，不会退回内存设备值。CAN 当前提供标准/扩展帧、CAN FD 标志、过滤器、持续接收和共享模拟总线，真实 ZLG、NI、Kvaser 等 SDK 仍未接入。

电源前台页面只操作设备实例；后台配置页面编辑型号 Profile 的通道名称、数据类型、读写权限和命令模板。配置保存为本机 Profile 覆盖，恢复默认不会修改插件代码。

## 手动和自动访问

手动界面通过共享设备运行时轮询测量通道。自动执行时通过 OpenTAP Host 的 `channel_request` 请求主程序设备服务；自动租约会先按实际通道自动连接所需实例，再调用同一个驱动。自动步骤执行到读写动作时才发送设备指令，手动轮询和手动读写在自动占用期间暂停，租约结束后恢复可见且已连接面板的轮询。

只有检测到旧的 `%LOCALAPPDATA%\TestStation\scpi-power-supply.json` 连接文件时，才会在首次打开工程时迁移为 `ITECH01` 电源实例及一个 TCP 资源，并保留原有序列通道 ID。没有旧文件的新工程保持空设备列表；新建设备不会自动绑定资源。新的实例使用 `ITECH02.<通道>` 等独立通道 ID；通道标识不能在后台直接重命名，已有通道只能停用以保护序列引用。

首期内置型号目录来自参考工程中 IT6000、IT6000B、IT6000C、IT6000D 和 IT6000PV 的本地 `ModelCatalog`，公共模拟配置明确标记为“公共 SCPI 模拟配置”，不能据此宣称已完成真实型号实机验证。ITECH 页面暂不选择 VISA/CAN，但通用层已提供它们的会话接口和模拟验收入口。





## HIOKI PW6001 功率分析仪

HIOKI PW6001 已作为第二个应用内设备家族接入。当前只启用 TCP 资源和 SCPI 协议，设备实例使用 `hioki.pw6001` 家族标识，默认型号为 PW6001。每个实例有 6 个测量通道，每个通道提供电压 RMS、电流 RMS、有功功率、视在功率、无功功率、功率因数、频率和相位八个只读自动测试通道。

驱动按单次查询生成 `:MEASure? Urms{n},Irms{n},P{n},S{n},Q{n},PF{n},DEG{n},FREQ{n}`，连接初始化发送 `*IDN?`、`*CLS` 和 `:HEADer OFF`。响应支持普通数字以及 `NAN`、`O.R.`、`S.Err`、`-----` 和状态十六进制标记；非法响应会进入错误状态并释放会话和资源租约。手动页面只在可见时轮询当前实例的测量通道，自动运行仍通过组合设备后端和同一个协调器访问。真实 PW6001、串口和 VISA 尚未验证；本阶段不注册串口、VISA 或 CAN 连接。
