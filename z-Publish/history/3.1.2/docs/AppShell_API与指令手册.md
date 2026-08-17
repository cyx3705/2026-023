# AppShell API 与指令手册

> 适用版本：AppShell 3.1.2（3.0.3 为冻结基线；3.1.1 为上一版功能收口）

本手册给出稳定公开 API 的常用入口和 3.1.2 框架基础命令。完整签名以包内 `lib/<TFM>/AppShell.*.xml` 为准；源码仓中的四份 `PublicAPI.Shipped.txt` 是冻结门禁，不随运行包发布。最终命令集合以应用运行时的 `command.list`、`command.show` 和 `command.manual` 为准。

模块命令、消费方业务命令以及按面板、MCP、Web 能力启用的命令不会在每个宿主中同时出现。

## 1. 包与命名空间

| 包 | 目标框架 | 主要命名空间 | 用途 |
|---|---|---|---|
| `OneHistory.AppShell.Core` | `net8.0` | `AppShell.Core.*` | 命令、停靠、日志、面板、模块 UI、MCP 元数据契约 |
| `OneHistory.AppShell.Services` | `net8.0` | `AppShell.Services.*` | 文件状态、日志、模块、MCP/Web 服务 |
| `OneHistory.AppShell.Shell` | `net8.0-windows` | `AppShell.Shell.*` | WPF Shell、AvalonDock 封装、控制台、面板和管理视图 |
| `OneHistory.AppShell.ServiceHost` | `net8.0-windows` | `AppShell.ServiceHost.*` | 无窗口 WPF 服务循环、生命周期和登录自启 |

桌面应用通常只直接引用 Shell；它会传递引入 Core 和 Services。需要独立服务入口时再直接引用 ServiceHost。

## 2. 最小桌面宿主

```csharp
using AppShell.Core;
using AppShell.Core.Commands;
using AppShell.Core.Docking;
using AppShell.Services;
using AppShell.Shell;
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
Shell 核心、窗口和业务命令；仍保留中央命令集与 `command.*`。需要可选能力时由消费方
明确设置，例如 `EnableModules = true` 或 `EnableMcp = true`。显式启用 MCP 后默认按 `mcp.autostart` 启动；若只
需要装配命令和治理能力而不希望启动时监听，应预先设置 `mcp.autostart=false`，之后可执行 `mcp.start`。

## 3. 常用公开 API

### 3.1 身份与路径

| API | 常用成员 | 说明 |
|---|---|---|
| `AppIdentity` | `Current`、`From(Assembly)`、`Use(Assembly)` | 统一应用名和版本；应在创建网关前确定 |
| `AppPaths` | `Root`、`LogsDir`、`ModulesDir`、`PanelsDir` | 建立 `%AppData%/<应用名>` 下的标准路径 |
| `SettingsService` | `Get`、`GetInt`、`Set`、`All` | JSON 设置持久化 |
| `FileLayoutStore` | `ReadCurrent`、`WriteCurrent`、`ReadNamed`、`WriteNamed` | 当前布局与命名布局存储 |
| `ShellLog` | `Log`、`Snapshot`、`EntryAdded` | 文件与内存日志；使用后 `Dispose` |

### 3.2 命令

| API | 常用成员 | 说明 |
|---|---|---|
| `CommandRegistry` | `Register`、`Unregister`、`TryGet`、`All`、`Suggest`、`GetSource` | 权威命令注册表；重名注册会拒绝 |
| `CommandBus` | `Validate`、`ExecuteAsync`、`Executed`、`Confirmation` | 唯一执行入口，统一校验、确认、线程切换、回显和错误结果 |
| `CommandDescriptor` | `Name`、`Summary`、`Example`、`Parameters`、`Readonly`、`Dangerous`、`ExecutionSite`、`AllowMcpExecution` | 命令的完整合同 |
| `CommandContext` | `RequireString`、`GetString`、`GetInt`、`GetDouble`、`GetBool`、`Has` | 读取已校验参数 |
| `CommandResult` | `Ok`、`Fail`、`Success`、`Message`、`Data` | 统一执行结果 |
| `CommandSchemaExporter` | `ExportTools`、`Find`、`BuildCommandText` | 从最终注册表生成 MCP schema 和反向命令文本 |
| `CommandManualGenerator` | `Render`、`Sha256` | 从运行时注册表生成命令手册 |

注册命令时至少提供名称、摘要和 handler；公开给用户或 MCP 的命令还应提供参数说明与示例。

```csharp
registry.Register(new CommandDescriptor
{
    Name = "device.move",
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

### 3.3 窗口与布局

| API | 常用成员 | 说明 |
|---|---|---|
| `ToolWindowDescriptor` | `Id`、`Title`、`ContentFactory`、`DefaultSide`、`DefaultRatio`、`DefaultVisible`、`DefaultTabTarget` | 注册窗口的稳定描述符；未设置 `DefaultSide` 时默认右置 |
| `DockSide` | `Left`、`Right`、`Top`、`Bottom`、`Tab`、`Center` | `Center` 是中央主工作区；`Tab` 需要目标窗口 |
| `IDockingService` | `RegisterWindow`、`UnregisterWindow`、`UnregisterOwner`、`Show`、`Hide`、`Float`、`Dock`、`SetRatio` | 操作窗口，不直接接触 AvalonDock 类型 |
| `IDockingService` | `SaveLayout`、`LoadLayout`、`ListLayouts`、`ResetLayout` | 布局方案管理 |
| `ShellUiRegistrar` / `IShellUiRegistrar` | `RegisterToolWindow`、`UnregisterOwner`、`Invoke` | 模块安全注册 UI，并在卸载时按 owner 回收 |

`AppShell.Shell` 是唯一允许直接依赖 AvalonDock 的层。消费应用和模块只使用上述 AppShell 契约。

命令集默认注册为 `DockSide.Center`，并作为固定主文档：不能隐藏、浮动或停靠到四边。中央始终显示自己的
页面头和页面选择标签；多个 `Center` 窗口进入同一个文档标签组。`Show` 选中的业务中央页不会被命令集自愈
逻辑抢回焦点；业务中央窗口隐藏、浮动或卸载后，命令集仍留在主区。普通四边工具页也允许由用户拖入中央
页面选择区，并可再次拖回四边；其描述符、owner 和内容实例不变，布局保存/恢复会保留嵌入位置。
模块注册窗口未设置 `DefaultSide` 时默认使用 `DockSide.Right`；模块可按业务需要显式改为
`Left/Top/Bottom/Center/Tab`，AppShell 不覆盖模块的显式声明。运行期注册或停靠到同一侧的窗口复用该侧已有标签组；
例如模块使用默认位置或显式以 `DockSide.Right` 注册时，直接成为右侧窗口
标签，不会在右侧再切出独立子窗格。该侧不存在窗格时才创建新窗格。
加载历史布局时，同侧的多个旧窗格也会合并为一个标签组；左右或上下侧栏合计不超过 50%，中央主工作区
始终至少占对应轴的 50%。

顶栏移动按宿主归属区分：中央文档页头空白移动整个 AppShell；普通嵌入工具页头空白执行 `win.float` 并让新浮窗继续
跟随鼠标。进入专注后，文档页和工具页的空白顶栏都只移动整个 AppShell；拖出专注页必须拖真实页签，由页签路径
执行 `win.restore` → `win.float`。独立浮窗内容位于单独的 `PresentationSource`，AppShell 通过窗格样式
把同一顶栏协调器带入该宿主；浮窗空白只移动该浮窗，重新停靠必须拖动真实页签并使用
AvalonDock 原生停靠流程。工具页动作区不提供浮窗最大化/还原按钮；文档浮窗保留自身状态按钮，该按钮不等同于
`win.max`，也不会改变 AppShell 专注布局。

消费方仍只使用 `ToolWindowDescriptor` 和 `IDockingService`，不得直接依赖内部 AvalonDock 文档类型。枚举值
固定为 `Tab=4`、`Center=5`，保证旧模块的 `Tab` 二进制值不会漂移。

### 3.4 面板与模块

| API | 常用成员 | 说明 |
|---|---|---|
| `PanelDefinition` | `Id`、`Title`、`Side`、`Ratio`、`Visible`、`Controls` | JSON 面板模型 |
| `PanelManager` | `Definitions`、`Reload`、`TrySetValue` | 面板发现和运行时值更新 |
| `ModuleHost` | `Attach`、`Start`、`Reload`、`ChangeDirectory`、`Modules` | 隔离装载命令/UI 模块；使用后 `Dispose` |
| `IUiModule` | `CreateUi`、`DestroyUi` | UI 模块生命周期 |
| `IShellUiAware` | `ShellUi` | 注入宿主 UI 注册器 |

### 3.5 MCP、Web 与 ServiceHost

| API | 常用成员 | 说明 |
|---|---|---|
| `McpGateway` | `Start`、`Stop`、`TryAutostart`、`VisibleTools`、`Port`、`IsRunning` | MCP 生命周期与最终工具面；使用后 `Dispose` |
| `PromptGovernanceStore` | `CreateProposal`、`ApproveProposal`、`ApplyProposal`、`RejectProposal`、`RevertToRevision` | 文件化提示词治理 |
| `WebGateway` | `Start`、`Stop`、`RelayFrontendCommandAsync`、`RequestWebConfirmation`、`DisconnectDevice` | HTTP/WebSocket 和前端命令中继 |
| `ShellServiceClient` | `PairAsync`、`ReconnectAsync`、`WaitForReadyAsync`、`ExecuteAsync`、`RunEventLoopAsync` | Shell 前端连接服务并自动发布命令目录 |
| `ServiceComposition` | `Registry`、`Bus`、`Settings`、`Log`、`Mcp`、`Web`、`Modules`、`DeferredWork` | 无窗口服务的组合根 |
| `ServiceHost` | `Run(ServiceComposition, ...)` | 启动服务循环，注册生命周期并统一释放 |
| `IAutostartManager` | `IsEnabled`、`SetEnabled` | 登录自启抽象；Windows 实现为 `WindowsRunAutostartManager` |

## 4. 命令语法与执行位置

语法为：

```text
域.动作 [位置参数] [键=值]
```

- 名称与参数名不区分大小写；名称在注册时规范为小写。
- 含空格的值使用双引号，内部引号使用 `\"`。
- `#` 开始注释；脚本由 `run` 逐行执行。
- 命令日志回显由解析结果重新生成：命令名规范为小写，位置参数与命名参数按解析结果的枚举顺序输出，
  并用标准引号规则重新转义。因此回显用于表达实际执行语义，不保证保留用户输入的原始大小写、空白或参数排列；
  敏感参数值会替换为 `[REDACTED]`；敏感命令返回的非字符串结构化 `Data` 不对外透传。
- `CommandHistory.Add` 是低层公开入口，只能接收已经脱敏的命令回显；框架控制台只把总线生成的
  `cmd:手动` 脱敏回显写入历史。3.0 历史文件带 `# AppShell.CommandHistory.v2:redacted` 头，首次启动时会清空
  没有该头的旧格式历史，避免 3.0 以前可能保存的明文再由 `history` 返回。
- 所有 UI、脚本、Web 和 MCP 调用最终都进入 `CommandBus.ExecuteAsync`。
- `ExecutionSite=Local` 在当前宿主执行；`Frontend` 由服务转发给在线 Shell。
- `Readonly`、`Dangerous` 和 `AllowMcpExecution` 是安全合同。前端/UI 命令默认不进入 MCP。

## 5. 基础命令目录

以下是 3.1.2 框架命令快照。宿主只注册已启用能力对应的组；运行时 `command.list` 是最终权威目录。

### 5.1 基础、应用与日志

| 命令 | 用途 / 关键参数 |
|---|---|
| `help [command]` | 列出命令或显示详情 |
| `cls` | `log.clear` 的兼容别名；新代码使用 `log.clear` |
| `history [count]` | 查看命令历史 |
| `run file= [continue=false]` | 执行命令脚本；失败时默认停止 |
| `app.exit` | 正常关闭桌面应用 |
| `app.frontend.show`、`app.frontend.hide` | 显示/隐藏前端窗口并保持后台服务连接 |
| `app.frontend.focus-console` | 唤出前端并聚焦控制台命令框；无前端时由后台启动 |
| `app.frontend.exit` | 只退出前端进程；`app.exit` 才协调前后台一起退出 |
| `app.about` | 显示应用身份与版本 |
| `app.get [key]` | 读取一个或全部设置；`code`、token、password/passwd、secret、private key 与 connection string 类设置只返回 `(已配置)`，不返回明文 |
| `app.set key= value=` | 写设置；上述敏感键的结果同样只返回 `(已配置)` |
| `app.opendata` | 打开应用数据目录 |
| `app.window [state=normal|minimized|maximized|toggle]` | 查询或设置主窗口状态 |
| `log.level [level=trace|debug|info|warn|error|fatal]` | 无参数时查看当前控制台日志级别；带参数时修改显示级别 |
| `log.source [source=全部|UI|手动|脚本|layout|日志]` | 查询或设置来源过滤 |
| `log.keyword [text=...]` | 查询或设置关键字过滤 |
| `log.mute [layout=true|false]` | 查询或设置 layout 来源屏蔽 |
| `log.autoscroll [enabled=true|false]` | 查询或设置自动滚动（默认开启） |
| `log.clear`、`log.export [path=]`、`log.copy` | 清屏、导出或复制当前控制台内容 |
| `log.focus [errors=true|false]` | 聚焦控制台，可选切换错误过滤 |

### 5.2 窗口与布局

| 命令 | 用途 / 关键参数 |
|---|---|
| `win.list` | 列出窗口状态、位置与比例 |
| `win.show name=`、`win.hide name=`、`win.float name=`、`win.autohide name=`、`win.reset name=` | 显示、隐藏、浮动、切换自动隐藏或复位窗口；固定命令集主文档拒绝隐藏/浮动 |
| `win.max name=`、`win.restore` | 最大化单窗或恢复整体布局 |
| `win.dock name= pos=left|right|top|bottom|center|tab [target=] [ratio=]` | 停靠窗口；Center 进入主文档区，tab 需要目标；命令集只允许 Center；提供 ratio 时须满足 `0 < ratio < 1` |
| `win.ratio name= value=` | 设置四边窗口占主窗体比例，须满足 `0 < value < 1`；中央页不支持比例调整 |
| `layout.save name=`、`layout.load name=` | 保存或载入命名布局 |
| `layout.list`、`layout.reset` | 列出方案或恢复默认布局 |

### 5.3 面板

面板能力存在时注册 `panel.*`。文件/目录选择也通过命令总线执行，视图按钮不得直接调用对话框服务。

| 命令 | 用途 / 关键参数 |
|---|---|
| `panel.list` | 列出面板 |
| `panel.show id=` | 显示面板窗口 |
| `panel.set panel= control= value=` | 更新面板控件值 |
| `panel.reload` | 重载已有面板定义 |
| `panel.select-file` | 打开文件选择器并返回选中的路径 |
| `panel.select-directory` | 打开目录选择器并返回选中的路径 |

### 5.4 模块

仅在 `ShellConfig.EnableModules=true` 时注册。AppShell 独立可执行宿主显式启用此项并显示唯一的“模块管理”页；
普通包消费方仍按最小能力原则选择是否启用。消费方不得复制 `ModuleHost` 或 `ModulesView`，只声明停靠位置和业务模块。

| 命令 | 用途 / 关键参数 |
|---|---|
| `module.list` | 列出模块、版本、槽和命令数 |
| `module.reload` | 重新发现并装载模块 |
| `module.dir [path]` | 查看或切换模块目录 |
| `module.open` | 在资源管理器中打开模块目录 |

模块公开方法另外注册为 `<模块名>.<方法名>`，不属于固定基础命令。

### 5.5 命令目录与 MCP

`command.*` 是 Shell 核心能力，始终注册；`mcp.*` 仅在消费方显式设置 `ShellConfig.EnableMcp=true` 时注册。

| 命令 | 用途 / 关键参数 |
|---|---|
| `command.list [mcp=all|visible|hidden]` | 查看权威命令目录并按当前 MCP 可见性过滤；默认 `all` |
| `command.show name=` | 查看单条命令完整元数据 |
| `command.domains` | 按域统计命令 |
| `command.manual file= [apply=false]` | 生成运行时命令手册；`file` 必填，apply 由宿主控制落位 |
| `mcp.start [port]`、`mcp.stop`、`mcp.status` | MCP 网关生命周期 |
| `mcp.schema [name]` | 查看全部或单个 MCP JSON Schema |
| `mcp.parse command= [args=|argsfile=] [exec=false]` | 调试 MCP 参数到命令文本的反向解析 |
| `app.get key=mcp.*`、`app.set key=mcp.* value=` | 查看或修改 `mcp.port/token/policy/confirm/autostart` 等设置；框架不注册同名的独立 MCP 配置命令 |

### 5.6 提示词治理

| 命令 | 用途 |
|---|---|
| `mcp.desc` | 本地直接修订工具描述 |
| `prompt.get`、`prompt.history`、`prompt.diff`、`prompt.propose` | 查看描述、历史、差异和提交提案 |
| `correction.list`、`correction.propose` | 查看或提交描述勘误 |
| `incident.list`、`incident.record` | 查看或记录调用/描述事故 |
| `mcp.pending`、`mcp.approve`、`mcp.reject`、`mcp.apply`、`mcp.revert` | 审核、应用或回退治理记录 |

治理命令的 id、reviewer、reason、evidence 等完整参数以 `command.show <name>` 为准，避免客户端复制一套可漂移参数表。

### 5.7 Web 与服务生命周期

Web 组合注册 `web.*`；`ServiceHost.Run` 注册 `svc.*`。

| 命令 | 用途 / 关键参数 |
|---|---|
| `web.status` | 查看实际绑定、端口和连接数 |
| `web.bind [value]`、`web.token [value]`、`web.cors [value]` | 查看或修改 Web 设置 |
| `web.confirm [mode=local|web]` | 查看或设置 Web 确认模式 |
| `svc.status` | 查看服务、MCP、Web 和模块状态 |
| `svc.stop`、`svc.restart` | 请求停止或重启服务 |
| `svc.autostart [mode=on|off]` | 无参数时查看状态；`on` / `off` 修改登录自启 |
| `shortcut.list` | 查看 owner、手势和目标命令；不返回原始键盘事件 |

## 6. MCP 暴露规则

命令可查阅不等于允许 MCP 执行。最终可见性由 `CommandDescriptor`、`McpExposurePolicy` 和模块策略共同决定：

- `readonly` 策略只开放明确只读且未硬排除的命令。
- `standard` 可增加不需要本地确认的普通命令。
- 危险命令不是第三种策略；仅在 `standard` 且 `mcp.confirm=host` 时进入工具列表，并仍须由宿主确认，远端参数不能绕过。
- `ExecutionSite=Frontend` 的命令必须同时 `AllowMcpExecution=true`，并且目标前端在线。
- 多个前端在线而未指定 `_frontend` 时返回歧义错误，不随机选择。

## 7. 已删除的旧接口

3.1.2（延续 3.1.1 收口）不再提供 `IWorkspaceService`、`WorkspaceService`、`RemoteWorkspaceService`、`ShellConfig.Workspace`、
`ResourceView`、`StandardWindowIds.Resource` 或 `res.*`。资源浏览和文件操作应由独立模块提供。
演示宿主也不再注册 `motor.*` 或生成电机面板。旧数据接口 `IDataService`、`SqliteDataService`、
`RemoteDataService`、`TableView`、`ShellConfig.DataService` 和 `db.*` 同样不提供。这些名称若仍出现在消费应用中，
说明迁移尚未完成，不应通过添加兼容空壳解决。

3.0.0 的消费变更、删除接口和迁移注意事项见 [AppShell 3.0 消费变更摘要](AppShell_3.0_消费变更摘要.md)。
