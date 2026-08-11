# HistoryVulcan API 与指令手册

> 适用版本：HistoryVulcan **3.3.2** 正式（已部署于 `z-HistoryVulcan`；3.1.8 不受支持）

本手册给出 3.3.2 正式公开 API 的常用入口和框架基础命令。正式宿主运行入口为
`host/HistoryVulcan.exe`，程序集 XML 文档位于同一 `host/` 目录；兼容框架包的完整签名位于
`lib/<TFM>/HistoryVulcan.*.xml`。源码仓中的四份 `PublicAPI.Shipped.txt` 是冻结门禁，不随运行宿主发布。
最终命令集合以应用运行时的 `vulcan.command.list`、`vulcan.command.show` 和 `vulcan.command.manual` 为准。

3.3.0（DEC-022）将内置命令一次硬切为 `vulcan.<类>.<方法>`（全小写、无连字符、不留别名），Domain=`vulcan`；
命令集表格列为域|类|方法|MCP|参数|说明。全局快捷键与命令工作台（目录会话、补全、命令集/详情）由 HistoryMercury 4.1.0 拥有；
无 Mercury 时双 `/` 与命令集/详情不可用。
**3.3.2（DEC-023）在此基础上把类收敛为九类、退役影子域 `debug`，并确立
模块注册名与指令域的去品牌前缀规则（见 §3.3.1）；3.4.0（DEC-025）恢复受控的两段直接方法与域聚焦。** 3.3.1 → 3.3.2 的逐条改名映射见
§3.3.4 与 `HistoryVulcan_消费变更摘要.md`。当前正式部署版本为 **3.3.2**（位于 `z-HistoryVulcan`）；当前源码候选为 **3.5.0**，新增稳定语义命令 `vulcan.app.focusconsole`。
3.1.9 是旧名 AppShell 的最后快照，已随 3.2.0 发布退役；3.1.8 不作为稳定支持版本。以下包表和最小宿主代码
描述当前正式合同，但正式部署不提供 NuGet feed。

模块命令、消费方业务命令以及按面板、MCP、Web 能力启用的命令不会在每个宿主中同时出现。

## 1. 包与命名空间

| 包 | 目标框架 | 主要命名空间 | 用途 |
|---|---|---|---|
| `OneHistory.HistoryVulcan.Core` | `net8.0` | `HistoryVulcan.Core.*` | 命令、停靠、日志、面板、模块 UI、MCP 元数据契约 |
| `OneHistory.HistoryVulcan.Services` | `net8.0` | `HistoryVulcan.Services.*` | 文件状态、日志、模块、MCP/Web 服务 |
| `OneHistory.HistoryVulcan.Shell` | `net8.0-windows` | `HistoryVulcan.Shell.*` | WPF Shell、AvalonDock 封装、控制台、面板和管理视图 |
| `OneHistory.HistoryVulcan.ServiceHost` | `net8.0-windows` | `HistoryVulcan.ServiceHost.*` | 无窗口 WPF 服务循环、生命周期和登录自启 |

桌面应用通常只直接引用 Shell；它会传递引入 Core 和 Services。需要独立服务入口时再直接引用 ServiceHost。

## 2. 最小桌面宿主

```csharp
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Services;
using HistoryVulcan.Shell;
using System.Windows.Controls;

var paths = new AppPaths("MyProduct");
var settings = new SettingsService(paths);
var layouts = new FileLayoutStore(paths);
var log = new ShellLog(paths);

var config = new ShellConfig
{
    AppName = "MyProduct",
    AppVersion = AppIdentity.Current.Version,
};

config.ToolWindows.Add(new ToolWindowDescriptor
{
    Id = "main",
    Title = "主工作区",
    DefaultSide = DockSide.Center,
    DefaultRatio = 1,
    ContentFactory = () => new TextBlock { Text = "MyProduct 工作区" },
});

config.ConfigureCommands = registry => registry.Register(new CommandDescriptor
{
    Name = "project.refresh",
    Summary = "刷新当前项目",
    Readonly = true,
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("已刷新")),
}, "app");

var window = new ShellWindow(config, layouts, log, settings, paths.Root);
window.Show();
```

应用退出时应正常关闭 `ShellWindow`，并释放自己持有的 `ShellLog`、网关和模块宿主。强杀进程不会保证布局与历史完成写入。

`EnableModules`、`EnableUiModules`、`EnableMcp` 和 `EnableRemoteManagementViews` 均默认 `false`。上例只启动
Shell 核心、窗口和业务命令；仍保留 `vulcan.command.*`。中央命令集/详情与双 `/` 依赖 HistoryMercury 4.1.0。
需要可选能力时由消费方明确设置，例如 `EnableModules = true` 或 `EnableMcp = true`。

### `EnableMcp` 只是装配，不是监听

`EnableMcp = true` 的含义是**装配网关并注册 `vulcan.mcp.*` 指令**，它本身不打开任何端口。
是否监听由两个显式开关决定，二者都不硬编码：

| 开关 | 作用域 | 命令 |
| --- | --- | --- |
| 本次会话 | 立即开/关端口，不改设置 | `vulcan.mcp.start` / `vulcan.mcp.stop` |
| 持久 | 是否随宿主启动自动监听 | `vulcan.mcp.autostart enabled=true\|false` |

`mcp.autostart` **缺省为 `false`**：网关会对外开一个回环端口，开不开必须由人显式决定，
不能因为装配了就默默监听。`vulcan.mcp.start` 只影响本次会话，不会顺手把持久自启动也打开。

HistoryVulcan 桌面宿主自 3.4.0 起 `EnableMcp = true`，因此 `vulcan.mcp.*` 始终可用，
但默认不监听——要连接先执行 `vulcan.mcp.start`。

## 3. 命令总线如何消费

消费方与模块只应通过命令总线交互，不要绕过注册表直接调用业务方法。

### 3.1 唯一执行入口

`CommandBus` 是唯一执行入口：

- `Validate(text)`：只做解析与参数绑定校验，不执行、不写日志。
- `ExecuteAsync(text, source)`：解析 → 查表 → 绑定 → 确认闸口 → UI 线程编组 / 前端中继 / 远端路由 → 返回 `CommandResult`。
  `source` 为来源标签（如 `UI`、`手动`、`脚本:文件名`、`layout`）。

```csharp
// 校验
var error = bus.Validate("vulcan.ui.show name=console");
if (error != null) { /* 语法或参数问题 */ }

// 执行
var result = await bus.ExecuteAsync("vulcan.ui.show name=console", "UI");
```

所有 UI、脚本、Web 和 MCP 调用最终都进入 `CommandBus.ExecuteAsync`。

### 3.2 注册

通过 `CommandRegistry.Register(descriptor, source)` 注册。描述符至少提供 `Name`、`Summary`、`Handler`；
面向用户或 MCP 的命令还应声明 `Domain`、`CommandClass`、`Parameters`、`Example` 等。

```csharp
registry.Register(new CommandDescriptor
{
    Name = "device.move",
    Domain = "DeviceModule",
    CommandClass = "motion",
    Summary = "移动指定轴",
    Example = "device.move axis=X distance=10",
    Parameters =
    [
        new ParameterSpec { Name = "axis", Required = true, Position = 0, AllowedValues = ["X", "Y", "Z"] },
        new ParameterSpec { Name = "distance", Type = ParamType.Double, Required = true, Position = 1 },
    ],
    Dangerous = true,
    ConfirmPrompt = ctx => $"确认移动 {ctx.RequireString("axis")}?",
    Handler = async ctx => await MoveAsync(ctx),
}, "app");
```

模块侧优先经 `IModuleContext.RegisterCommands` 登记；宿主提交时以 `module:<模块名>` 为 source，
并由 owner **强制**模块域（描述符中填其他 Domain 也不能冒用，且按 §3.3.1 去掉 `History` 前缀）。
反射方法可用 `ModuleCommandAttribute` 声明 `CommandClass` / `Readonly`。
3.4.0（DEC-025）起合法命名恰有两种形态：

| 形态 | 含义 | 类 |
| --- | --- | --- |
| `<域>.<类>.<方法>` | 业务指令的默认形态 | 第二段 |
| `<域>.<方法>` | 该域的**无类直接方法**，为快捷方法保留 | 无类 |

类推导是纯结构判定：段数 ≥ 3 取第二段；段数 = 2 判为无类（**不再回退首段作为类**）；
描述符显式声明的 `CommandClass` 永远优先。

对 3.3.2 的行为变更：两段名此前被读作「类.方法」，现读作「域.方法」。
若你的模块注册了两段式指令（如 WBall 的 `arena.*`），命令名与调用方式不变，
但在命令集与控制台里的归类显示会从具体类改为「无类」，
且其首段会成为一个**已注册域**，参与域聚焦的绝对名解析。

### 3.3 命名规则（3.4.0 起）

指令名全小写、**无连字符**、**不留别名**，合法形态只有两种：业务指令使用
`<域>.<类>.<方法>`，域的直接方法使用 `<域>.<方法>`。两段直接方法只用于少量快捷入口，
例如 `mercury.go`；它们在目录和控制台中显示为「无类」，但不参与类推导。

- 框架内置命令：`vulcan.<类>.<方法>`，`Domain` 恒为 `vulcan`，类取 §3.3.2 九类之一。
- 模块命令默认使用 `<模块域>.<类>.<方法>`；确需直接方法时可使用 `<模块域>.<方法>`。
  模块域由 owner 强制（见 §3.3.1），模块不能冒用其他域。
- 旧别名 `cls` 已删除；清屏仅 `vulcan.log.clear`。
- 方法段不使用连字符：例如 `floatstate`、`copyexample`、`selectfile`、`layoutsave`。

#### 3.3.1 模块注册名与指令域：域名去掉 `History` 品牌前缀

**这是 3.3.2 必须先读懂的一条规则：模块的注册名不再和指令域名称对齐。**

OneHistory 产品族的模块一律叫 `HistoryXxx`（`HistoryJanus`、`HistoryMercury`、
`HistoryMinerva`、`HistoryVulcan`）。把 `History` 放在**模块名**前面是统一的、正确的——
它标识产品族归属。但把同一个 `History` 带进**指令域**就完全多余：域段会出现在每一次
指令输入、每一行命令目录、每一份 MCP schema 和每一条日志回显里，而 `History` 在那些位置
不携带任何区分信息——所有模块都叫 `History` 开头，等于没说。

因此：

| 项 | 取值 | 例 |
|---|---|---|
| 模块名（manifest `name`、程序集、目录、Z 快照） | **保留** `History` 前缀 | `HistoryJanus` |
| 指令域（命令名首段、`Domain`、目录「域」列） | **去掉** `History` 前缀后的小写主体 | `janus` |

```
HistoryJanus    → janus     janus.project.commit
HistoryMercury  → mercury   mercury.shortcut.list
HistoryMinerva  → minerva   minerva.<类>.<方法>
HistoryVulcan   → vulcan    vulcan.command.list
WBall           → wball     wball.<类>.<方法>          （无品牌前缀者原样小写）
```

归一化由 Core 的 `ModuleDomainNaming.ToDomain(moduleName)` 承担，是唯一真值：

- 大小写不敏感地剥离开头的 `History`，其余部分转小写；
- 剥离后为空则退回原名小写（防止出现名为 `History` 的模块被归一化成空域）；
- 不以 `History` 开头的模块名原样转小写。

`ModuleHost` 在强制 owner 域时调用同一函数，因此模块**无需也无法**自行声明域：
描述符里写什么 `Domain` 都会被 owner 归一化结果覆盖。模块作者只需保证 manifest 的
`name` 正确，域自动得出。

> 消费方注意：这条规则改变的是**域段文本**，不是模块身份。`z-*` 目录名、manifest `name`、
> 程序集名、日志 owner 字段继续使用带前缀的 `HistoryXxx`。

#### 3.3.2 九个类

3.3.2 将 13 个类收敛为 9 个，并退役影子域 `debug`；3.4.0 起「无类」作为两段直接方法的受控显示类别回归：

| 类 | 条数 | 职责 |
|---|---|---|
| `app` | 11 | 应用与前端生命周期、外观、配置项、数据目录、快捷键查阅 |
| `command` | 8 | 指令目录、详情、手册、示例与执行原语 |
| `ui` | 21 | 窗口、布局、面板与文件选择对话框 |
| `log` | 11 | 控制台日志过滤、导出与承压注入 |
| `mcp` | 12 | MCP 网关（含 start/stop/autostart 开关）与提案审批 |
| `module` | 4 | 模块发现、装载与管理页 |
| `prompt` | 8 | 提示词治理、纠正与事故记录 |
| `svc` | 4 | 后台服务生命周期 |
| `web` | 5 | Web 网关与设备确认 |

合计 83 条。模块自定义类不受这九类约束——九类是 `vulcan` 域内的划分。模块应在自己的域内
用同样的方式收敛，避免每个功能点单开一类；若同一域的直接方法超过 5 条，应重新评估是否建类。

#### 3.3.3 直接方法是例外而非常态

业务指令仍应并入语义最接近的既有类，不能为了「就一条指令」随意省略类段：

- 查阅全局快捷键 → `vulcan.app.shortcuts`（不是 `vulcan.listshortcuts`）
- 日志承压注入 → `vulcan.log.flood`（不是 `debug.logflood`）

`mercury.go` 这类两段直接方法用于切换域聚焦等少量横切快捷入口，不承载普通业务分支。
「无类」只是显示标签；类推导仍只看结构，段数为 2 即无类，段数 ≥ 3 取第二段。

#### 3.3.4 3.3.1 → 3.3.2 改名速查

`core`→`command`、`frontend`→`app`、`win`/`layout`/`panel`→`ui`，五条无类指令归类，
`debug.logflood`→`vulcan.log.flood`。完整 32 条改名映射见
`HistoryVulcan_消费变更摘要.md` 与仓库内 `b-Office/history/3.3.2-vulcan-class-realign.md`。
未改名的 51 条保持原文本。

### 3.4 发现

权威目录命令（Shell 核心，始终注册）：

| 命令 | 用途 |
|---|---|
| `vulcan.command.list` | 结构化目录（可按 domain/class/mcp/filter 过滤） |
| `vulcan.command.show` | 单条完整元数据与参数 |
| `vulcan.command.domains` | 按域统计 |
| `vulcan.command.manual` | 生成运行时命令手册 |
| `vulcan.command.copyexample` | 复制示例到剪贴板 |

`vulcan.command.list` 返回的行含 `Domain`、`CommandClass`、`Method`（末段方法名）以及 MCP/风险等字段。
注册表辅助：`CommandRegistry.GetMethod`（同 `LegacyMethod`）、`LegacyDomain` / `LegacyClass` / `LegacyMethod`
可从命令名推导域/类/方法段。

### 3.5 UI 与 Mercury

| 能力 | 归属 |
|---|---|
| 目录数据权威 | 宿主 `CommandRegistry`，经 `vulcan.command.list` / `domains` / `show` |
| 命令集 / 详情 / 补全会话 | **HistoryMercury 4.1.0**（实现 `ICommandCatalogSession`，经 `IShellCommandWorkbenchHost` 挂接） |
| 全局快捷键宿主 | Core 合同 `IGlobalShortcutHost`；实现与双 `/` 注册由 Mercury 提供 |
| Shell | 控制台日志面；Mercury 未挂接前仅为延迟会话代理 |

命令集表格列为：**域 | 类 | 方法 | MCP | 参数 | 说明**。

双 `/` 由 Mercury 注册，目标直接绑定 `vulcan.app.focusconsole`。该语义命令统一负责服务端冷启动/中继、
前端显示、控制台输入聚焦与控制台最大化；`mercury.shortcut.wakeconsole` 仅保留为兼容入口。
无 Mercury 时：双 `/`、中央命令集与详情不可用；`vulcan.command.*` 等总线命令仍可执行。
3.4.0 起两段直接方法显示为「无类」；`mercury.go` 是首个正式用例。

### 3.6 安全与执行位点（简要）

- `Dangerous` / `ConfirmPrompt`：危险元数据与本地确认闸口；未注入确认服务时带确认位的命令拒绝执行。
- `RequiresUiThread`：总线经 `UiContext` 编组到 UI 线程。
- `ExecutionSite`：`Local` 在当前宿主执行；`Frontend` 由服务转发给在线 Shell。
- `FrontendCommandCapability`：前端→服务的可序列化能力描述（无 Handler）；`From` / `CreateProxy` 用于跨进程目录与前端代理命令。

## 4. 常用公开 API

### 4.1 身份与路径

| API | 常用成员 | 说明 |
|---|---|---|
| `AppIdentity` | `Current`、`From(Assembly)`、`Use(Assembly)` | 统一应用名和版本；应在创建网关前确定 |
| `AppPaths` | `Root`、`LogsDir`、`ModulesDir`、`PanelsDir` | 建立 `%AppData%/<应用名>` 下的标准路径 |
| `SettingsService` | `Get`、`GetInt`、`Set`、`All` | JSON 设置持久化 |
| `FileLayoutStore` | `ReadCurrent`、`WriteCurrent`、`ReadNamed`、`WriteNamed` | 当前布局与命名布局存储 |
| `ShellLog` | `Log`、`Snapshot`、`EntryAdded` | 文件与内存日志；使用后 `Dispose` |

### 4.2 命令

| API | 常用成员 | 说明 |
|---|---|---|
| `CommandRegistry` | `Register`、`Unregister`、`TryGet`、`All`、`Suggest`、`GetSource`、`GetDomain`、`GetCommandClass`、`GetMethod`、`LegacyDomain`、`LegacyClass`、`LegacyMethod` | 权威命令注册表；重名注册会拒绝；域/类以有效解析结果为准；`GetMethod` 等同末段方法名（`LegacyMethod`） |
| `CommandBus` | `Validate`、`ExecuteAsync`、`Executed`、`Confirmation` | 唯一执行入口，统一校验、确认、线程切换、回显和错误结果 |
| `CommandDescriptor` | `Name`、`Domain`、`CommandClass`、`Summary`、`Example`、`Parameters`、`Readonly`、`Dangerous`、`ConfirmPrompt`、`RequiresUiThread`、`ExecutionSite`、`AllowMcpExecution`、`Handler` | 命令的完整合同 |
| `CommandContext` | `RequireString`、`GetString`、`GetInt`、`GetDouble`、`GetBool`、`Has` | 读取已校验参数 |
| `CommandResult` | `Ok`、`Fail`、`Success`、`Message`、`Data` | 统一执行结果 |
| `CommandSchemaExporter` | `ExportTools`、`Find`、`BuildCommandText` | 从最终注册表生成 MCP schema 和反向命令文本 |
| `CommandManualGenerator` | `Render`、`Sha256` | 从运行时注册表生成命令手册 |
| `FrontendCommandCapability` | `From`、`CreateProxy`、`Domain`、`CommandClass` | 前端可序列化能力；代理描述符 `ExecutionSite=Frontend` |
| `ICommandCatalogSession` | `RefreshAsync`、`SetFilter`、`CompleteAsync`、`Select`、… | 命令目录会话合同（Core）；由 Mercury 实现并挂接 |
| `IShellCommandWorkbenchHost` | `AttachCommandCatalogSession`、`Bus`、`ConfigureCommandCompletionRouting`、… | Shell 工作台宿主合同（Core）；`ShellWindow` 实现 |
| `IGlobalShortcutHost` | `Register`、`Start`、`Stop`、`Registrations`、… | 全局快捷键宿主合同（Core）；Mercury 实现 |

模块宿主的增量公开面如下：

| API | 常用成员 | 说明 |
|---|---|---|
| `IModuleContext` | `Bus`、`Log`、`Settings`、`DataDirectory`、`RegisterCommands` | 模块取得宿主权威服务和宿主数据根目录；模块自行在根目录下选择专属子目录 |
| `IModuleContextAware` | `Attach(IModuleContext)` | 模块声明需要宿主上下文；由 `ModuleHost` 在装载阶段调用 |
| `ModuleHost` | `Attach(registry, bus, settings, dataDirectory)` | 为模块生命周期接入完整宿主上下文；可注入 `CommandWorkbench` / `GlobalShortcuts` |
| `ShellConfig` | `ModuleDirectory` | 可选的部署模块目录；未设置时沿用应用数据目录 |

注册命令时至少提供名称、摘要和 handler；公开给用户或 MCP 的命令还应提供参数说明与示例。完整示例见 §3.2。

### 4.3 窗口与布局

| API | 常用成员 | 说明 |
|---|---|---|
| `ToolWindowDescriptor` | `Id`、`Title`、`ContentFactory`、`DefaultSide`、`DefaultRatio`、`DefaultVisible`、`DefaultTabTarget` | 注册窗口的稳定描述符；未设置 `DefaultSide` 时默认右置 |
| `DockSide` | `Left`、`Right`、`Top`、`Bottom`、`Tab`、`Center` | `Center` 是中央主工作区；`Tab` 需要目标窗口 |
| `IDockingService` | `RegisterWindow`、`UnregisterWindow`、`UnregisterOwner`、`Show`、`Hide`、`Float`、`Dock`、`SetRatio` | 操作窗口，不直接接触 AvalonDock 类型 |
| `IDockingService` | `SaveLayout`、`LoadLayout`、`ListLayouts`、`ResetLayout` | 布局方案管理 |
| `ShellUiRegistrar` / `IShellUiRegistrar` | `RegisterToolWindow`、`UnregisterOwner`、`Invoke` | 模块安全注册 UI，并在卸载时按 owner 回收 |

`HistoryVulcan.Shell` 是唯一允许直接依赖 AvalonDock 的层。消费应用和模块只使用上述 HistoryVulcan 契约。

命令集默认注册为 `DockSide.Center`，并作为固定主文档：不能隐藏、浮动或停靠到四边。中央始终显示自己的
页面头和页面选择标签；多个 `Center` 窗口进入同一个文档标签组。`Show` 选中的业务中央页不会被命令集自愈
逻辑抢回焦点；业务中央窗口隐藏、浮动或卸载后，命令集仍留在主区。普通四边工具页也允许由用户拖入中央
页面选择区，并可再次拖回四边；其描述符、owner 和内容实例不变，布局保存/恢复会保留嵌入位置。
模块注册窗口未设置 `DefaultSide` 时默认使用 `DockSide.Right`；模块可按业务需要显式改为
`Left/Top/Bottom/Center/Tab`，HistoryVulcan 不覆盖模块的显式声明。运行期注册或停靠到同一侧的窗口复用该侧已有标签组；
例如模块使用默认位置或显式以 `DockSide.Right` 注册时，直接成为右侧窗口
标签，不会在右侧再切出独立子窗格。该侧不存在窗格时才创建新窗格。
加载历史布局时，同侧的多个旧窗格也会合并为一个标签组；左右或上下侧栏合计不超过 50%，中央主工作区
始终至少占对应轴的 50%。

顶栏移动按宿主归属区分：普通嵌入页和专注页的空白顶栏只移动整个 HistoryVulcan，独立浮窗的空白顶栏只移动该浮窗。
主 HistoryVulcan 处于普通状态时，空白顶栏越过系统拖动阈值即开始移动，不增加按住延时。主窗口最大化状态及独立浮窗仍需按住满 120ms；最大化宿主使用两倍阈值，开始移动时按鼠标横向比例恢复。
一次按下只要越过移动阈值，就不再作为双击的第一次点击；下一次点击必须重新开始双击序列。
只有真实页签能够把页面拖出：普通页签沿用 AvalonDock 原生流程，专注页签执行 `vulcan.ui.restore` → `vulcan.ui.float`。
浮窗使用恢复后嵌入窗格的实际宽高并保持鼠标在原页签抓取点，跨显示器时按目标显示器 DPI 和工作区定位；重新停靠仍须
拖动真实页签。工具页动作区不提供浮窗最大化/还原按钮；文档浮窗保留自身状态按钮，该按钮不等同于 `vulcan.ui.max`，
也不会改变 HistoryVulcan 专注布局。

消费方仍只使用 `ToolWindowDescriptor` 和 `IDockingService`，不得直接依赖内部 AvalonDock 文档类型。枚举值
固定为 `Tab=4`、`Center=5`，保证旧模块的 `Tab` 二进制值不会漂移。

### 4.4 面板与模块

| API | 常用成员 | 说明 |
|---|---|---|
| `PanelDefinition` | `Id`、`Title`、`Side`、`Ratio`、`Visible`、`Controls` | JSON 面板模型 |
| `PanelManager` | `Definitions`、`Reload`、`TrySetValue` | 面板发现和运行时值更新 |
| `ModuleHost` | `Attach`、`Start`、`Reload`、`ChangeDirectory`、`Modules` | 隔离装载命令/UI 模块；使用后 `Dispose` |
| `IUiModule` | `CreateUi`、`DestroyUi` | UI 模块生命周期 |
| `IShellUiAware` | `ShellUi` | 注入宿主 UI 注册器 |

### 4.5 MCP、Web 与 ServiceHost

| API | 常用成员 | 说明 |
|---|---|---|
| `McpGateway` | `Start`、`Stop`、`TryAutostart`、`VisibleTools`、`Port`、`IsRunning` | MCP 生命周期与最终工具面；使用后 `Dispose` |
| `PromptGovernanceStore` | `CreateProposal`、`ApproveProposal`、`ApplyProposal`、`RejectProposal`、`RevertToRevision` | 文件化提示词治理 |
| `WebGateway` | `Start`、`Stop`、`RelayFrontendCommandAsync`、`RequestWebConfirmation`、`DisconnectDevice` | HTTP/WebSocket 和前端命令中继 |
| `ShellServiceClient` | `PairAsync`、`ReconnectAsync`、`WaitForReadyAsync`、`ExecuteAsync`、`RunEventLoopAsync` | Shell 前端连接服务并自动发布命令目录 |
| `ServiceComposition` | `Registry`、`Bus`、`Settings`、`Log`、`Mcp`、`Web`、`Modules`、`DeferredWork` | 无窗口服务的组合根 |
| `ServiceHost` | `Run(ServiceComposition, ...)` | 启动服务循环，注册生命周期并统一释放 |
| `IAutostartManager` | `IsEnabled`、`SetEnabled` | 登录自启抽象；Windows 实现为 `WindowsRunAutostartManager` |

## 5. 命令语法与执行位置

语法为：

```text
域.动作 [位置参数] [键=值]
```

- 名称与参数名不区分大小写；名称在注册时规范为小写。
- 含空格的值使用双引号，内部引号使用 `\"`。
- `#` 开始注释；脚本由 `vulcan.command.run` 逐行执行。
- 命令日志回显由解析结果重新生成：命令名规范为小写，位置参数与命名参数按解析结果的枚举顺序输出，
  并用标准引号规则重新转义。因此回显用于表达实际执行语义，不保证保留用户输入的原始大小写、空白或参数排列；
  敏感参数值会替换为 `[REDACTED]`；敏感命令返回的非字符串结构化 `Data` 不对外透传。
- `CommandHistory.Add` 是低层公开入口，只能接收已经脱敏的命令回显；框架控制台只把总线生成的
  `cmd:手动` 脱敏回显写入历史。3.0 历史文件带 `# HistoryVulcan.CommandHistory.v2:redacted` 头，首次启动时会清空
  没有该头的旧格式历史，避免 3.0 以前可能保存的明文再由 `vulcan.command.history` 返回。
- 所有 UI、脚本、Web 和 MCP 调用最终都进入 `CommandBus.ExecuteAsync`。
- `ExecutionSite=Local` 在当前宿主执行；`Frontend` 由服务转发给在线 Shell。
- `Readonly`、`Dangerous` 和 `AllowMcpExecution` 是安全合同。前端/UI 命令默认不进入 MCP。
- 3.2.1 起一个宿主或模块对应一个域，域内功能分支对应命令类。3.3.0 起内置命令名为 `vulcan.<类>.<方法>`，
  Domain=`vulcan`；命令集表格列为域|类|方法|MCP|参数|说明。
  `CommandCatalogRow.Source/SourceDetail`、`CommandRegistry.GetSource` 与 `FrontendCommandCatalog.Source`
  仍表示注册来源，供结构化目录、模块管理和外部消费者使用，不再作为命令集页面筛选。
- 控制台与命令集都通过统一注册表元数据读取同一份运行期已注册域集合。普通日志类别的第一个 `:` 或 `.`
  前缀只有命中已注册域时才用于过滤，否则归入 `core`，不会产生控制台私有域。命令结果/进度类别在
  `CommandBus.ResultCategory` / `ProgressCategory` 兼容前缀后附加命令域。长文本按当前窗格宽度软换行，复制和导出保留原始逻辑文本。
- 3.3.0 起命令工作台（目录会话、补全引擎、命令集/详情视图）与全局快捷键由 HistoryMercury 4.1.0 拥有；
  目录数据仍来自 `vulcan.command.list` / `vulcan.command.domains`，参数详情由 `vulcan.command.show` 延迟加载。
  Shell 仅保留控制台日志面。无 Mercury 时双 `/` 与命令集/详情不可用；有 Mercury 时，控制台聚焦态
  （如 `vulcan.ui.max name=console`）仍可弹出候选，`Shift+W`/`Shift+S`/`Tab`/`Enter` 行为不变。

## 6. 基础命令目录

以下是 HistoryVulcan **3.3.2 正式**框架命令快照（83 条）。宿主只注册已启用能力对应的组；
运行时 `vulcan.command.list` 是最终权威目录。
3.3.1→3.3.2 的 32 条改名映射见 `../history/3.3.2-vulcan-class-realign.md`；
更早的 3.3.0 硬切见 `../history/3.3.0-vulcan-command-rename.md`。

HistoryVulcan 自身只有一个域 `vulcan`，内置业务命令分为九类；新增内置业务命令必须归入其一，
名称恒为 `vulcan.<类>.<方法>`。两段直接方法是受控例外，不得引入第二个内置域：

| 域 | 类 | 条数 | 方法（命令范围） |
|---|---|---|---|
| `vulcan` | `app` | 11 | `vulcan.app.*`：身份、主题、设置、数据目录、前端生命周期、快捷键查阅 |
| `vulcan` | `command` | 8 | `vulcan.command.*`：目录、详情、手册、示例、`help`/`run`/`history` |
| `vulcan` | `ui` | 21 | `vulcan.ui.*`：停靠窗口、命名布局、面板、文件对话框 |
| `vulcan` | `log` | 11 | `vulcan.log.*`（无 `cls` 别名；含承压 `flood`） |
| `vulcan` | `mcp` | 11 | `vulcan.mcp.*` |
| `vulcan` | `module` | 4 | `vulcan.module.*` |
| `vulcan` | `prompt` | 8 | `vulcan.prompt.*`：描述治理、勘误与事故 |
| `vulcan` | `svc` | 4 | `vulcan.svc.*` |
| `vulcan` | `web` | 5 | `vulcan.web.*` |
| `mercury` | `shortcut` | — | `mercury.shortcut.*`（由 HistoryMercury 注册，不属本手册合同） |

3.3.1 的 `core`、`frontend`、`win`、`layout`、`panel` 五个类与影子域 `debug` 已退役。

> **诊断指令不进正式命令集。** 影子域 `debug` 退役后，承压注水被收编为 `vulcan.log.flood`，
> 但它不是给最终用户的功能：默认不注册，需宿主显式设置 `diagnostics.commands=true`；
> 即便注册，也标记为危险指令并被 MCP/Web 硬排除。上表 83 条不含它。

### 6.1 基础、应用与日志

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.command.help [command]` | 列出命令或显示详情 |
| `vulcan.command.history [count]` | 查看命令历史 |
| `vulcan.command.run file= [continue=false]` | 执行命令脚本；失败时默认停止 |
| `vulcan.app.quit` | 正常关闭桌面应用（协调前后台一起退出） |
| `vulcan.app.show`、`vulcan.app.hide` | 显示/隐藏前端**进程窗口**并保持后台服务连接；与 `vulcan.ui.show`/`hide`（停靠窗口）不同 |
| `vulcan.app.focusconsole` | 显示前端、打开并聚焦控制台且最大化；服务端无前端时以 `--focus-console` 冷启动 |
| `vulcan.app.close` | 只关闭前端进程，后台服务继续运行 |
| `vulcan.app.shortcuts` | 查看已注册全局快捷键的 owner、手势和目标命令；不返回原始键盘事件 |
| `vulcan.app.about` | 显示应用身份与版本 |
| `vulcan.app.get [key]` | 读取一个或全部设置；`code`、token、password/passwd、secret、private key 与 connection string 类设置只返回 `(已配置)`，不返回明文 |
| `vulcan.app.set key= value=` | 写设置；上述敏感键的结果同样只返回 `(已配置)` |
| `vulcan.app.opendata` | 打开应用数据目录 |
| `vulcan.app.window [state=normal|minimized|maximized|toggle]` | 查询或设置主窗口状态 |
| `vulcan.app.theme mode=light|dark|toggle` | 切换浅色/深色主题 |
| `vulcan.log.level [level=trace|debug|info|warn|error|fatal]` | 无参数时查看当前控制台日志级别；带参数时修改显示级别 |
| `vulcan.log.source [source=<已注册域>|全部]` | 查询或设置控制台域过滤；域变化会让类收敛到该域的有效类 |
| `vulcan.log.class [class=<当前域的类>|全部]` | 查询或设置当前域的命令类；域为“全部”时类固定为“全部” |
| `vulcan.log.keyword [text=...]` | 查询或设置关键字过滤 |
| `vulcan.log.mute [layout=true|false]` | 查询或设置 layout 来源屏蔽 |
| `vulcan.log.autoscroll [enabled=true|false]` | 查询或设置自动滚动（默认开启） |
| `vulcan.log.clear`、`vulcan.log.copy` | 清屏或复制当前控制台内容 |
| `vulcan.log.export [path=]` | 导出当前控制台可见内容。**省略 `path` 时不弹对话框**：写入应用数据目录 `exports/console-<时间戳>.txt` 并在结果中返回绝对路径（3.3.2 起） |
| `vulcan.log.focus [errors=true|false]` | 聚焦控制台，可选切换错误过滤 |
| `vulcan.log.flood rate= seconds=` | **诊断指令，默认不注册**：仅当宿主把设置 `diagnostics.commands` 置为 `true` 时才出现。标记为危险指令（走确认闸口），并由 `McpExposurePolicy` 硬排除，MCP/Web 永不可达。3.3.1 为 `debug.logflood` |

### 6.2 窗口、布局与面板（`ui`）

`ui` 是 3.3.2 合并 `win` / `layout` / `panel` 后的类，共 21 条。三个列表命令使用复数名词，
布局与面板动作使用 `layout*` / `panel*` 复合方法段，避免 `list` / `show` / `reset` 碰撞。

停靠窗口（11 条）：

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.ui.windows` | 列出窗口状态、位置与比例 |
| `vulcan.ui.show name=`、`vulcan.ui.hide name=`、`vulcan.ui.float name=`、`vulcan.ui.autohide name=`、`vulcan.ui.reset name=` | 显示、隐藏、浮动、切换自动隐藏或复位停靠窗口；固定命令集主文档拒绝隐藏/浮动 |
| `vulcan.ui.max name=`、`vulcan.ui.restore` | 最大化单窗或恢复整体布局 |
| `vulcan.ui.floatstate name= [state=maximized|normal|toggle]` | 设置独立浮窗宿主状态；页面按钮与浮窗标题双击使用同一命令 |
| `vulcan.ui.dock name= pos=left|right|top|bottom|center|tab [target=] [ratio=]` | 停靠窗口；Center 进入主文档区，tab 需要目标；命令集只允许 Center；提供 ratio 时须满足 `0 < ratio < 1` |
| `vulcan.ui.ratio name= value=` | 设置四边窗口占主窗体比例，须满足 `0 < value < 1`；中央页不支持比例调整 |

命名布局（4 条）：

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.ui.layouts` | 列出已保存的布局方案 |
| `vulcan.ui.layoutsave name=`、`vulcan.ui.layoutload name=` | 保存或载入命名布局 |
| `vulcan.ui.layoutreset` | 恢复默认布局 |

面板与文件对话框（6 条）：面板能力存在时注册。文件/目录选择也通过命令总线执行，
视图按钮不得直接调用对话框服务。

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.ui.panels` | 列出面板及其窗口状态 |
| `vulcan.ui.panelshow id=` | 显示面板窗口（等价 `vulcan.ui.show`） |
| `vulcan.ui.panelset panel= control= value=` | 更新面板控件值 |
| `vulcan.ui.panelreload` | 重载已有面板定义 |
| `vulcan.ui.selectfile` | 打开文件选择器并返回选中的路径 |
| `vulcan.ui.selectdirectory` | 打开目录选择器并返回选中的路径 |

### 6.4 模块

仅在 `ShellConfig.EnableModules=true` 时注册。HistoryVulcan 独立可执行宿主显式启用此项并显示唯一的“模块管理”页；
普通包消费方仍按最小能力原则选择是否启用。消费方不得复制 `ModuleHost` 或 `ModulesView`，只声明停靠位置和业务模块。
3.1.9 起模块管理页只读取 `vulcan.module.list`，用单一“刷新模块”按钮执行 `vulcan.module.reload` 后重新取清单；模块指令
详情统一在命令集页面查看，模块页不再读取 `vulcan.command.list` 或显示第二个指令表。

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.module.list` | 列出模块、版本、槽和命令数 |
| `vulcan.module.reload` | 重新发现并装载模块 |
| `vulcan.module.roots [paths=<绝对根1;绝对根2>|auto]` | 查询/设置 Z 模块发现根；`auto` 恢复向上识别 `HistoryVesta.git` |
| `vulcan.module.open` | 在资源管理器中打开模块目录 |

模块公开方法另外注册为 `<模块域>.<类>.<方法>`，模块域按 §3.3.1 去掉 `History` 前缀
（`HistoryJanus` → `janus.*`），不属于固定基础命令。

### 6.5 命令目录与 MCP

`vulcan.command.*` 是 Shell 核心能力，始终注册；`vulcan.mcp.*` 仅在消费方显式设置 `ShellConfig.EnableMcp=true` 时注册。

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.command.list [domain=] [class=] [mcp=all|visible|hidden] [filter=]` | 查看权威命令目录并组合过滤域、类、MCP 可见性和文本；默认不过滤；行含 Method |
| `vulcan.command.show name=` | 查看单条命令完整元数据 |
| `vulcan.command.domains` | 按域统计命令 |
| `vulcan.command.manual file= [apply=false]` | 生成运行时命令手册；`file` 必填，apply 由宿主控制落位 |
| `vulcan.command.copyexample name=` | 复制命令示例到剪贴板 |
| `vulcan.mcp.start [port]`、`vulcan.mcp.stop`、`vulcan.mcp.status` | MCP 网关生命周期 |
| `vulcan.mcp.schema [name]` | 查看全部或单个 MCP JSON Schema |
| `vulcan.mcp.parse command= [args=|argsfile=] [exec=false]` | 调试 MCP 参数到命令文本的反向解析 |
| `vulcan.app.get key=mcp.*`、`vulcan.app.set key=mcp.* value=` | 查看或修改 `mcp.port/token/policy/confirm/autostart` 等设置；框架不注册同名的独立 MCP 配置命令 |

### 6.6 提示词治理

| 命令 | 用途 |
|---|---|
| `vulcan.mcp.desc` | 本地直接修订工具描述 |
| `vulcan.prompt.get`、`vulcan.prompt.history`、`vulcan.prompt.diff`、`vulcan.prompt.propose` | 查看描述、历史、差异和提交提案 |
| `vulcan.prompt.corrections`、`vulcan.prompt.correct` | 查看或提交描述勘误 |
| `vulcan.prompt.incidents`、`vulcan.prompt.record` | 查看或记录调用/描述事故 |
| `vulcan.mcp.pending`、`vulcan.mcp.approve`、`vulcan.mcp.reject`、`vulcan.mcp.apply`、`vulcan.mcp.revert` | 审核、应用或回退治理记录 |

治理命令的 id、reviewer、reason、evidence 等完整参数以 `vulcan.command.show <name>` 为准，避免客户端复制一套可漂移参数表。

### 6.7 Web、服务生命周期与快捷键

Web 组合注册 `vulcan.web.*`；`ServiceHost.Run` 注册 `vulcan.svc.*`。全局快捷键由 HistoryMercury 拥有；
双 `/` 直接触发 `vulcan.app.focusconsole`。
查阅快捷键用 `vulcan.app.shortcuts`（3.3.1 的无类 `vulcan.listshortcuts`）。

| 命令 | 用途 / 关键参数 |
|---|---|
| `vulcan.web.status` | 查看实际绑定、端口和连接数 |
| `vulcan.web.bind [value]`、`vulcan.web.token [value]`、`vulcan.web.cors [value]` | 查看或修改 Web 设置 |
| `vulcan.web.confirm [mode=local|web]` | 查看或设置 Web 确认模式 |
| `vulcan.svc.status` | 查看服务、MCP、Web 和模块状态 |
| `vulcan.svc.stop`、`vulcan.svc.restart` | 请求停止或重启服务 |
| `vulcan.svc.autostart [mode=on|off]` | 无参数时查看状态；`on` / `off` 修改登录自启 |
| `vulcan.app.shortcuts` | 查看 owner、手势和目标命令；不返回原始键盘事件（属 `app` 类，见 §6.1） |
| `vulcan.app.focusconsole` | Mercury 双 `/` 的稳定目标；服务端中继或冷启动，前端聚焦并最大化控制台 |
| `mercury.shortcut.wakeconsole` | Mercury 兼容包装；单步调用 `vulcan.app.focusconsole` |

## 7. MCP 暴露规则

命令可查阅不等于允许 MCP 执行。最终可见性由 `CommandDescriptor`、`McpExposurePolicy` 和模块策略共同决定：

- `readonly` 策略只开放明确只读且未硬排除的命令。
- `standard` 可增加不需要本地确认的普通命令。
- 危险命令不是第三种策略；仅在 `standard` 且 `mcp.confirm=host` 时进入工具列表，并仍须由宿主确认，远端参数不能绕过。
- `ExecutionSite=Frontend` 的命令必须同时 `AllowMcpExecution=true`，并且目标前端在线。
- 多个前端在线而未指定 `_frontend` 时返回歧义错误，不随机选择。

## 8. 已删除的旧接口

3.1.2（延续 3.1.1 收口）不再提供 `IWorkspaceService`、`WorkspaceService`、`RemoteWorkspaceService`、`ShellConfig.Workspace`、
`ResourceView`、`StandardWindowIds.Resource` 或 `res.*`。资源浏览和文件操作应由独立模块提供。
演示宿主也不再注册 `motor.*` 或生成电机面板。旧数据接口 `IDataService`、`SqliteDataService`、
`RemoteDataService`、`TableView`、`ShellConfig.DataService` 和 `db.*` 同样不提供。这些名称若仍出现在消费应用中，
说明迁移尚未完成，不应通过添加兼容空壳解决。

3.0.0 的消费变更、删除接口和迁移注意事项见 [HistoryVulcan 消费变更摘要](HistoryVulcan_消费变更摘要.md)。
