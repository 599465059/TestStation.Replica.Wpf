# Python 协议管理功能复刻对照

只读参考：TestStationDemo/station。协议管理原有四个导航子节点现已进入当前 C# 主窗口，不使用 Python 子进程，也不修改原工程。

| 来源 | 功能 | C# 对应 | 状态 |
|---|---|---|---|
| protocol_management_page.py / application/protocol_service.py | 协议列表、详情、新建、删除、导入、导出 | ProtocolLibraryView / IProtocolLibraryService / ProtocolRepository | 已复刻 |
| protocol_domain.py | 协议→多帧→字段模型，角色/类型/字节序/比例/常量/通道引用 | Domain.Protocols | 已复刻，兼容 .tsproto v1 和历史字段别名 |
| viewmodels/protocol_editor_viewmodel.py | 帧新增/复制/命名/删除、字段编辑、选择、可见帧过滤 | IProtocolEditingService / ProtocolEditorViewModel | 已复刻，编辑先在副本上验证，失败不污染文档 |
| protocol_canvas.py | 多帧比较、字节尺寸、组件双击/拖入、字段拖动、互斥 | ProtocolEditorView / ProtocolFrameCanvas | 已复刻；字节宽度默认 54 DIP，显示数量 0 自动，超出滚动 |
| protocol_canvas.py | 帧属性、字段属性、示例常量组帧、HEX 按字段解析 | ProtocolFrameForm / ProtocolFieldForm / IProtocolPreviewService | 已复刻，属性溢出滚动 |
| application/dbc_service.py / infrastructure/dbc.py | DBC 导入去重、文件管理、整数信号编解码 | Infrastructure.Protocols.DbcService / Domain.Protocols.DbcSignal | 已复刻；同内容去重，同名不同内容编号，Intel/Motorola、符号、比例偏移 |
| dbc_page.py / viewmodels/dbc_viewmodel.py | 按发送 ECU / 接收 ECU / 未分类消息浏览，表格联动 | DbcBrowserView / DbcBrowserViewModel | 已复刻，13 个可调整列，比例和偏移分开；支持按 CAN ID、消息、信号和 ECU 筛选 |
| gallery.py | 协议库、自定义协议、DBC、协议测试子节点 | ProtocolModule / ProtocolPages / 主窗口 ProtocolHost | 已接通 |
| ProtocolTestPage（原占位） | 离线协议测试入口 | ProtocolTestView / ProtocolTestViewModel | 按确认范围接通现有示例组帧/字段解析，并接通 DBC 离线编解码 |

## 原工程未完成或本次未扩展的内容

| 缺项 | 原工程事实 | 当前处理 |
|---|---|---|
| 字段映射专用页面 | _field_mapping_tab 只有占位文字，实际设备绑定对话框在其他模块 | 保留 channel_id，字段属性可查看/编辑引用；不宣称已有完整设备通道映射器 |
| 在线协议测试 | ProtocolTestPage 只有占位，没有资源选择/收发实现 | 本次为离线测试，不连接 CAN/串口/TCP |
| 自定义帧长度自动计算约定 | 预览只写常量，未根据 length_field_id 推算长度值 | 保留配置，提示不自动推算，避免自行决定长度包含范围 |
| 自定义多字段帧校验运算 | 编辑器提供 CRC8/CRC16 等选项，预览未实现计算；旧通信 codec 的校验属于另一套绑定协议 | 保存算法和起点，示例预览明确标注不计算；后续需要确定算法参数、覆盖范围、校验字段字节序 |
| 自定义字段按物理量编码/解码 | 原帧预览只显示字段原始字节，数据类型、scale/offset 是定义配置 | 保持原已实现能力；不把原始字节伪装成解码后的物理值 |
| DBC 扩展信号 | 原简易解析器未实现完整多路复用、SIG_VALTYPE_ 浮点和扩展多路复用语义 | 可浏览基础定义，并显示警告；含相关扩展的数据库禁用离线编解码，避免输出错误结果 |
| DBC 生成器 | 协议管理原页面无自定义协议→DBC 生成实现 | 记录为后续能力，本次不新增 |

所有未知协议元数据保留；切换协议时可保存、放弃或取消。用户工程中的损坏协议逐文件记录错误，不阻止其他有效协议加载。

协议模型的 version 是正整数，文件封装的 version 固定为 1；不是语义版本字符串。原 Python 模型已读取并校验当前 C# 保存的文件，Python 原模型生成的双帧文件也作为独立测试夹具保存在 Smoke/Fixtures/python_protocol.tsproto。初次 C# 迁移产生的字符串整数版本（例如 "1.0"）会在读取时迁移，下一次保存统一写为整数。字段停用只改变参与组帧/重叠检测的状态，保留原模型的帧范围和字段数量约束。

## 验证入口

业务测试：`dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj`。

窗口测试：`dotnet run --no-build --project src/TestStation.Smoke/TestStation.Smoke.csproj -- --ui`。

测试使用临时工程；DBC 测试夹具复制自原 Python 工程。实际鼠标测试覆盖组件双击、组件拖入、块拖动、树切换；截图输出到 artifacts/ui。

