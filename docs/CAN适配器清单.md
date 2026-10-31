# CAN 厂商适配器清单

软件统一使用 `Drivers/CAN/<adapter>/manifest.json` 描述 CAN 厂商库的部署方式。

| 后端 | 库部署 | 当前状态 | 实机验证 |
|---|---|---|---|
| `pcan` | 随软件发布 `PCANBasic.dll` | PCAN-Basic 控制器会话已接入 | 已用四通道 PCAN 的通道 1、2 收发验证 |
| `zlg` | 随软件发布 `zlgcan.dll` 及其依赖 | native 库已加载检查；控制器会话待 ZLG 型号适配 | 当前无 ZLG 实物 |
| `kvaser` | 随软件发布 `canlib32.dll` | native 库已加载检查；控制器会话待 Kvaser 型号适配 | 当前无 Kvaser 实物 |
| `vector` | 随软件发布 `vxlapi64.dll` | native 库已加载检查；控制器会话待 Vector 驱动权限适配 | 当前无 Vector 实物 |
| `tosun` | 随软件发布 `libTSCAN.dll`、`libTSH.dll` | native 库已加载检查；控制器会话待型号适配 | 当前无同星实物 |
| `ni-xnet` | 必须安装 NI-XNET Runtime | 自动按安装目录和系统环境探测 | 当前机器未安装 |
| `etas` | 必须安装 ETAS BOA | 自动按 ETAS 安装规则探测 | 当前机器未安装 |

“native 库已加载检查”只证明 DLL 位数、依赖和加载路径可用，不等于对应硬件控制器已经完成。没有硬件的后端禁止退回模拟数据；连接时会返回明确的“控制器会话未接入”错误。

统一探测命令：

```powershell
dotnet run --project tools/TestStation.PcanProbe/TestStation.PcanProbe.csproj -- --all
```
