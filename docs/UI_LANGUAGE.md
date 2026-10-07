# 界面语言规范

当前界面以中文为主，文案与内部数据标识分离。

| 内容 | 规则 |
|---|---|
| 导航、菜单、面板、表头、属性、按钮、提示 | 中文 |
| SETUP / MAIN / CLEANUP | 初始化 / 主序列 / 清理 |
| 基础动作 | 初始化、设置值、读取值、等待、断言、调用序列、注释 |
| 流程关键字 | IF、THEN、ELSE、FOR、END IF、END FOR |
| 状态与日志等级 | 未运行、运行中、已暂停、通过、失败、已停止、已跳过；信息、警告、错误 |
| 专业名词、单位、变量标识 | 保留 CAN、DBC、SCPI、Modbus、TCP、JSON、ASCII 等术语和原始标识 |
| 用户名称、描述、导入数据 | 保留用户填写的原文 |

## 实现位置

- `Localization/UiText.resx`：集中保存静态文案和格式化文案。
- `Localization/UiText.cs`：XAML 与 C# 共用的文案入口。
- `Localization/UiDisplay.cs`：步骤、分组、状态、已知参数键、日志等级和协议配置选项的显示映射。
- `Converters/UiValueConverter.cs`、`UiOptionTemplate`：翻译显示内容；下拉框仍然选择原始值。

语言适配全部留在 App 层，Domain、Application、Infrastructure 不依赖文字资源或 WPF。没有修改控件库源码、原 Python 工程及参考工程。

## 数据兼容

变量 `limit` 的类型下拉框显示“整数”，文件仍保存 `integer`；作用域显示“局部变量”，文件仍保存 `local`。失败处理显示“继续执行”，步骤参数仍保存 `CONTINUE`。

协议帧类型 `read_request` 显示“读取请求”，字段角色 `header` 显示“帧头”，字节序 `big` 显示“大端”。保存格式继续采用 `.tsproto` 的原始标识。

已有序列中默认名称 `SET VALUE` 在表格和属性中显示“设置值”；打开工程不会改写原文件。新建步骤采用中文名称，类型仍为 `set_value`。用户自定义名称（包含英文名称）不进行全文替换。

程序名称 Test Station、变量值、表达式、JSON 和原始报文不翻译；第三方或操作系统返回的诊断保留原始信息，避免改写异常含义。

## 验证

使用原有编译、业务 Smoke 和窗口 UI 检查。窗口检查增加了实际 ComboBox 选项模板、选择绑定以及保存重新加载验证，确保中文显示未写入类型和作用域字段。既有拖拽、嵌套、复制粘贴、模拟运行、菜单、主题和停靠测试继续执行。
