# PCAN-Basic 适配器

当前工程已经把 PEAK PCAN-Basic 接入通用 `ICanBusSession`。PCAN 适配器由清单描述部署方式，并通过绝对路径加载 native 库，不依赖当前工作目录或用户手工复制 DLL。

默认发布目录为：

```text
Drivers/CAN/PCAN/manifest.json
Drivers/CAN/PCAN/x64/PCANBasic.dll
```

清单的 `loadMode` 为 `bundled` 时只从软件目录加载随软件发布的 DLL。必须由厂商安装的适配器使用 `installed`，由适配器清单提供系统路径、PATH 或厂商安装目录探测规则；找不到时返回明确的安装提示，不会退回内存模拟值。调试时可用 `TESTSTATION_DRIVER_ROOT` 覆盖适配器根目录。

## 支持范围

- 通过 PCAN-Basic 暴露的 USB、USB Pro、USB X6、PCI/PCIe、LAN、虚拟通道等设备；设备型号和通道数量由 `PCAN_ATTACHED_CHANNELS` 动态枚举。
- 经典 CAN 和 CAN FD；经典速率使用 PCAN-Basic 的标准 BTR 值，FD 使用明确的 80 MHz 时序配置。
- 标准帧、扩展帧、远程帧、BRS、只听模式、异步接收和多个订阅者。
- `PCAN_USBBUS1`、`PCAN_PCIBUS1`、`PCAN_LANBUS1`、十六进制句柄（例如 `0x0051`）和十进制句柄都可以作为资源通道。设备有多少通道由驱动实际返回，不按四通道写死。

## 工程资源配置

在资源管理页新增或选择 `CAN` 资源，设置：

```json
{
  "type": "can",
  "lease_mode": "shared_bus",
  "settings": {
    "can_backend_id": "pcan",
    "channel": "PCAN_USBBUS1",
    "bitrate": 500000,
    "fd_enabled": false,
    "listen_only": false
  }
}
```

四通道 PCAN 设备通常会枚举为 `PCAN_USBBUS1` 到 `PCAN_USBBUS4`，但软件不会假设一定是这四个通道。PCI、PCIe、LAN 和其他 USB 型号使用 PCAN-Basic 返回的通道句柄即可。

可以先使用只读探测工具查看驱动实际返回的所有通道：

```powershell
dotnet run --project tools/TestStation.PcanProbe/TestStation.PcanProbe.csproj
```

探测工具不会打开通道、修改波特率或发送报文。

如果通道 1 和通道 2 已经连接并且总线使用 500 kbit/s，可以运行一次真实收发检查：

```powershell
dotnet run --project tools/TestStation.PcanProbe/TestStation.PcanProbe.csproj -- --loopback
```

该检查通过正式 `TransportSessionFactory` 创建两个 `can_backend_id=pcan` 资源，通道 1 发送 `0x5A5 / DE AD BE EF`，通道 2 接收后退出。

缺少驱动、DLL 位数不匹配、通道被 PCAN-View 占用、设备不支持 FD、波特率不支持以及总线错误都会返回明确异常。软件不会把这些情况降级成模拟数据。

## 运行链路

```text
TransportResource
  → TransportSessionFactory
  → PcanCanBusSession
  → PcanVendorAdapter
  → PCANBasic.dll
```

一个 PCAN 会话只有一个底层接收循环，多个手动面板或协议订阅共享该会话。资源管理器负责共享总线租约，停止或断线时会关闭底层通道并释放租约。

本工程不复制参考仓库的页面、采集线程和反射工厂；PCAN-Basic DLL 按适配器清单随当前软件发布。若使用其他版本或其他厂商适配器，只需替换对应适配器目录及清单，并确认主程序与 DLL 都是 x64 或都为 x86。硬件驱动本身仍需按 PEAK 的许可和安装要求部署。
