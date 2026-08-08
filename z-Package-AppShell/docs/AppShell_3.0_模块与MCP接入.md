# AppShell 模块与 MCP 接入

> 适用版本：AppShell 3.1.9 候选；当前稳定消费版本为 3.1.7，3.1.8 不受支持
> 边界：本文只描述框架能力。项目库、外部账号、工具同步等消费产品业务不属于 AppShell。
> 常用公开方法和基础命令见 [AppShell API 与指令手册](AppShell_API与指令手册.md)。

## 组件边界

- `OneHistory.AppShell.Core`：命令、停靠、模块 UI、MCP 元数据与存储契约。
- `OneHistory.AppShell.Services`：日志、设置、布局、模块宿主、MCP 网关与 Web 网关。
- `OneHistory.AppShell.Shell`：WPF Shell、控制台、面板、模块管理与 MCP 管理视图。
- `OneHistory.AppShell.ServiceHost`：无窗口服务组合、`svc.*` 与登录启动管理。

桌面单进程应用可直接创建 `ShellWindow`。前后端分离应用在服务端建立自己的 `CommandRegistry`，前端使用
`ShellServiceClient.RunEventLoopAsync(localBus)`；WebSocket 建立后，客户端会自动发布完整本地命令目录，
服务端据此创建 `ExecutionSite=Frontend` 的动态代理。消费方不得维护第二份前端命令名清单。

## 模块宿主

`ShellConfig.EnableModules` 默认为 `false`。消费方明确需要模块命令扫描和热重载时设置为 `true`；只需要
加载声明了 `ui=true` 的模块界面时可单独设置 `EnableUiModules=true`。模块目录默认是
`%AppData%/<应用名>/Modules`：根目录 DLL 走
兼容装载；每个一级子目录是独立模块槽并拥有可回收的 `AssemblyLoadContext`。依赖优先从本槽解析，宿主
不会把更深目录当作新模块槽。

模块以 `BaseVariable.ModuleInfoBase` 派生类型描述名称、版本、启用状态与方法暴露。公共、非泛型、非属性
访问器方法映射为 `<模块名>.<方法名>`；相邻 XML 文件为 Help、命令目录和 MCP schema 提供摘要。命令重名
时拒绝新项，不覆盖框架、应用或其他模块命令。

3.1.9 起，需要宿主服务的外置模块实现 `IModuleContextAware`。装载后宿主调用 `Attach(IModuleContext)`，
上下文提供权威 `CommandBus`、`IShellLog`、`ISettingsService` 和宿主数据根目录；模块应在该根目录下使用
自身专属子目录。模块通过
`RegisterCommands` 注册的命令仍由模块 owner 在卸载时统一回收。独立宿主可设置
`ShellConfig.ModuleDirectory` 可显式指向其他部署目录；未设置时继续使用应用数据目录下的默认模块目录。
禁用模块不会收到上下文；`Attach`、`RegisterShortcuts`、`CreateUi` 和 `DestroyUi` 等生命周期方法不会进入
反射命令目录。模块不得保存上下文供卸载后使用，也不得自行创建第二个命令总线或设置服务。

UI 模块实现 `IUiModule`；需要注册宿主窗口时实现 UI 感知接口并使用 `IShellUiRegistrar`。窗口使用
`ToolWindowDescriptor` 注册，中央业务窗口显式指定 `DockSide.Center`。模块卸载时先销毁 UI、注销 owner
命令和窗口，再释放加载上下文。文件监听器对 `.dll`、`.xml`、`.panel.json` 去抖后整体重载；坏模块只下线
自身，不拖垮宿主。

模块的 `ToolWindowDescriptor.DefaultSide` 未设置时默认为 `DockSide.Right`；模块仍可显式指定其他方位，
AppShell 不覆盖该声明。3.0.2 起，模块运行期注册的右侧窗口直接加入现有右侧标签组，不再为每个模块另建一块右侧
窗格。模块无需填写 `DefaultTabTarget`；执行窗口复位或 `win.dock ... pos=right` 也沿用同一合并规则。只有
当前布局完全没有右侧窗格时，框架才创建新的右侧窗格。

3.1.1 起，历史布局中已经存在的同侧独立窗格也会在加载时合并成一个标签组；同一轴的侧栏合计最多占 50%，
中央主工作区至少保留 50%。模块不应通过额外侧栏规避该主区保护规则。

命令集是固定中央主文档。模块中央窗口通过 `DockSide.Center` 进入同一个文档标签组；中央自己的页面头和
页面选择标签始终显示，模块页可通过标签或 `IDockingService.Show` 切换。模块卸载、隐藏或浮动时，命令集仍留在主区。模块不得自行维护另一套首页
或页面生命周期，也不得直接依赖内部 AvalonDock 文档类型。

模块注册的普通四边工具页同样允许用户拖入中央标签组并再拖回；嵌入期间仍是原工具页，由 owner 注销和模块
卸载统一回收。模块不需要、也不得为“可嵌入中央”另建页面描述符。

模块可附带 `<模块名>.panel.json`。框架同步到应用 `panels` 目录的副本使用 `module-` 前缀；模块下线只回收
这类同步副本，不触碰用户手写面板。

## MCP 网关

`ShellConfig.EnableMcp` 默认为 `false`。默认 Shell 不创建 `McpGateway`、提示词治理存储或 MCP 审计器，
不注册 `mcp.*` / `prompt.*` / `correction.*` / `incident.*`，也不监听端口。本地 `command.*` 与中央命令集
仍可使用。消费方显式设置 `EnableMcp=true` 后才装配上述能力；若同时设置 `mcp.autostart=false`，启动时只装配
不监听，之后可用 `mcp.start` 启动。

MCP 启用后的默认端口为 `8737 + stableHash(appName) % 200`。显式 `mcp.port` 优先；端口占用时按
`mcp.portretries` 有界顺延，默认尝试 20 次。网关停止时释放监听、会话和取消令牌。

支持的协议版本为 `2025-06-18` 与 `2025-03-26`。工具结果同时保留文本 `content`，对象或数组结果通过
`structuredContent.data` 投影。无效协议头被拒绝，未知 initialize 版本回落到框架支持的最新版。

命令是否进入 MCP 由最终 `CommandDescriptor` 与策略共同决定：

- `readonly`：只暴露 `Readonly=true` 且未被硬排除的命令。
- `standard`：额外暴露不需要本地确认的普通命令。
- 危险命令不是第三种策略：仅在 `standard` 且 `mcp.confirm=host` 时进入工具列表，并仍需宿主确认；远端参数不能绕过。
- 前端 UI 命令默认不进入 MCP；只有 `AllowMcpExecution=true` 才允许投影。
- 模块的 `readonly` / `standard` / `hidden` 档位由模块清单解释，硬排除规则优先。

`McpAuditRecorder` 与 `PromptGovernanceStore` 使用应用数据目录中的文件留痕，不依赖数据库。会话缓存由
`mcp.sessionlimit` 限制；超时只切断当前 MCP 响应，不会强行中止已经进入宿主的命令，后续应通过只读命令
查询结果。

## Web 与前端目录

Web 默认端口为 `8938 + stableHash(appName) % 200`，支持 `web.port` 与 `web.portretries`。默认只应绑定
回环地址；远程绑定需要显式鉴权、设备配对和来源限制。限流窗口与前端目录缓存分别受
`web.ratewindowlimit`、`web.frontendcataloglimit` 约束。

每个前端目录携带 session id 与应用名。调用前端命令时可用 `_frontend=<session id 或 app name>` 定向；
只有一个在线前端时自动选择；多个前端且未指定时明确失败。前端离线后目录仍可查阅，但执行明确失败。
WebSocket 支持分片文本消息，总消息上限 1 MiB。

## 安全纪律

1. `Readonly`、危险性、参数、执行站点和 MCP 许可只在生产 `CommandDescriptor` 中声明。
2. UI、Help、Web、MCP 与命令手册都从最终 `CommandRegistry` 投影，不复制名单。
3. 危险操作必须由宿主确认；`--yes`、HTTP 参数或 MCP 参数都不能绕过远程确认。
4. token、密码、私钥和连接串不得写入命令结果、日志或审计文件。
5. 3.1.9 是当前源码候选；3.1.8 不作为稳定支持版本。全局 z 级模块扫描仍不在本版本范围。
   双进程服务历史设计不随消费包发布。

## 最小验收

- `module.list` 能显示名称、版本、槽和命令数；坏 DLL 或缺依赖只影响对应模块。
- 模块命令在 Help、命令目录与 MCP schema 中保持同源；服务端本地模块热重载后会更新本地权威目录。
  Shell 前端在连接后动态注册的命令需要重连，才会重新发布到服务端权威目录。
- 前端连接后目录自动出现；多前端歧义、定向、中断和离线执行行为符合上述规则。
- MCP/Web 默认端口在同机多应用间稳定分离，冲突顺延有界，Stop 后端口可重新绑定。
