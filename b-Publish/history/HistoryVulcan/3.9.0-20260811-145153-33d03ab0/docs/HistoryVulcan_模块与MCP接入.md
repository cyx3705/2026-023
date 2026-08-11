# HistoryVulcan 模块与 MCP 接入

> 适用版本：HistoryVulcan **3.3.2** 正式（已部署于 `z-HistoryVulcan`；3.1.8 不受支持）
> 3.3.0（DEC-022）：内置命令硬切为 `vulcan.<类>.<方法>`（Domain=`vulcan`）；全局快捷键（含 `GlobalShortcutService`）与命令工作台由 HistoryMercury 4.1.0 拥有。
> **3.3.2（DEC-023）：类收敛为九类；模块指令域 = 模块名去掉 `History` 前缀（`HistoryJanus` → `janus.<类>.<方法>`），由 `ModuleDomainNaming.ToDomain` 归一化；类不可省略。**
> 旧→新映射见 `../history/3.3.0-vulcan-command-rename.md` 与 `../history/3.3.2-vulcan-class-realign.md`。
> 边界：本文只描述框架能力。项目库、外部账号、工具同步等消费产品业务不属于 HistoryVulcan。
> 常用公开方法和基础命令见 [HistoryVulcan API 与指令手册](HistoryVulcan_API与指令手册.md)。

## 组件边界

- `OneHistory.HistoryVulcan.Core`：命令、停靠、模块 UI、MCP 元数据与存储契约。
- `OneHistory.HistoryVulcan.Services`：日志、设置、布局、模块宿主、MCP 网关与 Web 网关。
- `OneHistory.HistoryVulcan.Shell`：WPF Shell、控制台日志面、面板、模块管理与 MCP 管理视图；命令集/详情/补全由 Mercury 挂接，挂接前为 `DeferredCommandCatalogSession`。
- `OneHistory.HistoryVulcan.ServiceHost`：无窗口服务组合、`vulcan.svc.*` 与登录启动管理。

桌面单进程应用可直接创建 `ShellWindow`。前后端分离应用在服务端建立自己的 `CommandRegistry`，前端使用
`ShellServiceClient.RunEventLoopAsync(localBus)`；WebSocket 建立后，客户端会自动发布完整本地命令目录，
服务端据此创建 `ExecutionSite=Frontend` 的动态代理。消费方不得维护第二份前端命令名清单。

## 模块宿主

`ShellConfig.EnableModules` 默认为 `false`。正式独立宿主开启后台 Z 发现和命令生命周期；消费方只加载声明了
`ui=true` 的 UI 部分时可单独设置 `EnableUiModules=true`。正式发现根自动向上定位 `HistoryVesta.git`，
每个根只枚举 `<project>/z-*`，不读取或监听 `%AppData%/<应用名>/Modules`。每个 Z 目录必须含显式
`module.manifest.json`，且 `schemaVersion=1`、`type=HistoryVulcan.Module`；`artifact`、`docs`、`deps`
是相对 manifest 所在 Z 目录且必须存在。模块名是生命周期 owner 和唯一命令域，文件夹名只用于定位候选。
`vulcan.module.roots` 可查询或设置分号分隔的绝对根，`paths=auto` 恢复自动识别；`vulcan.module.reload` 负责重扫、卸载旧快照、
装载有效入口并发布修订。错误 manifest、越界、缺入口、重名和身份/版本不符会跳过并写诊断。

模块以 `BaseVariable.ModuleInfoBase` 派生类型描述名称、版本、启用状态与方法暴露。公共、非泛型、非属性
访问器方法映射为 `<模块名>.<方法名>`；相邻 XML 文件为 Help、命令目录和 MCP schema 提供摘要。命令重名
时拒绝新项，不覆盖框架、应用或其他模块命令。

3.1.9 起，需要宿主服务的外置模块实现 `IModuleContextAware`。装载后宿主调用 `Attach(IModuleContext)`，
上下文提供权威 `CommandBus`、`IShellLog`、`ISettingsService` 和宿主数据根目录；模块应在该根目录下使用
自身专属子目录。模块命令优先经 `IModuleContext.RegisterCommands` 登记，仍由模块 owner 在卸载时统一回收。
反射方法可用 `ModuleCommandAttribute` 声明 `CommandClass` / `Readonly`（次要路径）。独立宿主可设置
`ShellConfig.ModuleDirectory` 可显式指向其他部署目录；未设置时继续使用应用数据目录下的默认模块目录。
禁用模块不会收到上下文；`Attach`、`RegisterShortcuts`、`CreateUi` 和 `DestroyUi` 等生命周期方法不会进入
反射命令目录。模块不得保存上下文供卸载后使用，也不得自行创建第二个命令总线或设置服务。

需要向 Shell 注入命令工作台（目录会话、命令集/详情页面）时，模块实现 `IShellCommandWorkbenchAware`；
宿主在 CreateUi 前注入 `IShellCommandWorkbenchHost`。页面与目录会话实现由 HistoryMercury 4.1.0 提供；
模块不得自建第二套命令集/详情/补全 UI。

### 模块命令域与类

3.2.1 起每个模块稳定名称就是该模块的唯一命令域。`ModuleHost` 会强制使用当前 module owner，
模块在 `CommandDescriptor.Domain` 中填写其他值也不能冒用其他域。全局命令文本仍保持唯一，分类不会改写
`<模块名>.<方法名>` 或模块自定义命令名。

模块内部功能分支通过 `CommandClass` 区分，类名使用小写稳定标识符。优先经 `IModuleContext.RegisterCommands`：

```csharp
context.RegisterCommands(registry => registry.Register(new CommandDescriptor
{
    Name = "historyvesta.timeline.list",
    CommandClass = "timeline",
    Summary = "列出时间线",
    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
}));
```

反射方法可用 `ModuleCommandAttribute`（次要）：

```csharp
[ModuleCommand(CommandClass = "report", Readonly = true)]
public string BuildReport() => "ready";
```

直接注册命令使用 `CommandDescriptor.CommandClass`，反射方法使用 `ModuleCommandAttribute.CommandClass`。
旧模块未声明类时统一归入 `core`；宿主外的旧非模块命令仍按旧命令前缀兼容推导。域和类经前后端目录同步，
命令集、控制台、Help、MCP 和生成手册读取同一结果。

UI 模块实现 `IUiModule`；需要注册宿主窗口时实现 UI 感知接口并使用 `IShellUiRegistrar`。窗口使用
`ToolWindowDescriptor` 注册，中央业务窗口显式指定 `DockSide.Center`。模块卸载时先销毁 UI、注销 owner
命令和窗口，再释放加载上下文。文件监听器对 `.dll`、`.xml`、`.panel.json` 去抖后整体重载；坏模块只下线
自身，不拖垮宿主。

模块的 `ToolWindowDescriptor.DefaultSide` 未设置时默认为 `DockSide.Right`；模块仍可显式指定其他方位，
HistoryVulcan 不覆盖该声明。3.0.2 起，模块运行期注册的右侧窗口直接加入现有右侧标签组，不再为每个模块另建一块右侧
窗格。模块无需填写 `DefaultTabTarget`；执行窗口复位或 `vulcan.ui.dock ... pos=right` 也沿用同一合并规则。只有
当前布局完全没有右侧窗格时，框架才创建新的右侧窗格。

3.1.1 起，历史布局中已经存在的同侧独立窗格也会在加载时合并成一个标签组；同一轴的侧栏合计最多占 50%，
中央主工作区至少保留 50%。模块不应通过额外侧栏规避该主区保护规则。

命令集是固定中央主文档（由 HistoryMercury 提供页面）。模块中央窗口通过 `DockSide.Center` 进入同一个文档标签组；中央自己的页面头和
页面选择标签始终显示，模块页可通过标签或 `IDockingService.Show` 切换。模块卸载、隐藏或浮动时，命令集仍留在主区。模块不得自行维护另一套首页
或页面生命周期，也不得直接依赖内部 AvalonDock 文档类型，更不得自建第二套命令目录 UI。

模块注册的普通四边工具页同样允许用户拖入中央标签组并再拖回；嵌入期间仍是原工具页，由 owner 注销和模块
卸载统一回收。模块不需要、也不得为“可嵌入中央”另建页面描述符。

模块可附带 `<模块名>.panel.json`。框架同步到应用 `panels` 目录的副本使用 `module-` 前缀；模块下线只回收
这类同步副本，不触碰用户手写面板。

## MCP 网关

`ShellConfig.EnableMcp` 默认为 `false`。默认 Shell 不创建 `McpGateway`、提示词治理存储或 MCP 审计器，
不注册 `vulcan.mcp.*` / `vulcan.prompt.*`（含勘误与事故命令），也不监听端口。本地 `vulcan.command.*` 仍可使用；
中央命令集/详情依赖 HistoryMercury 4.1.0，无 Mercury 时不可用。消费方显式设置 `EnableMcp=true` 后才装配上述能力；若同时设置 `mcp.autostart=false`，启动时只装配
不监听，之后可用 `vulcan.mcp.start` 启动。

HistoryVulcan 自身的单 EXE 双进程宿主是一个明确例外：前端保持 `EnableMcp=false`，后台服务在唯一权威
`CommandRegistry` 上装配 MCP、非 UI 模块与完整 `McpCommands`。后台先同步装载模块，再开放 Web/MCP，
因此首次 `tools/list` 已包含所有成功装载且策略可见的模块命令。嵌入式消费方原有
`ShellConfig.EnableMcp` 行为不变。

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

`tools/list` 和 `tools/call` 直接读取同一个后台注册表/总线，不维护模块名单。模块热重载后工具目录动态变化，
无需重启网关。独立宿主会把旧前端设置中的九个 `mcp.*` 键按“后台缺失才复制”迁移；远程
`vulcan.app.get/set` 只允许这些键，token、密码、私钥和 secret 只返回“已配置”。

## Web 与前端目录

Web 默认端口为 `8938 + stableHash(appName) % 200`，支持 `web.port` 与 `web.portretries`。默认只应绑定
回环地址；远程绑定需要显式鉴权、设备配对和来源限制。限流窗口与前端目录缓存分别受
`web.ratewindowlimit`、`web.frontendcataloglimit` 约束。失败鉴权仍按远端地址限流，普通鉴权 Web/远端
会话按会话限流；已认证的本机 `loopback-shell` 是双进程控制通道，不消耗公网 Web 请求配额。429 响应带
`Retry-After` 和 JSON 重试秒数。

每个前端目录携带 session id 与应用名。调用前端命令时可用 `_frontend=<session id 或 app name>` 定向；
只有一个在线前端时自动选择；多个前端且未指定时明确失败。前端离线后目录仍可查阅，但执行明确失败。
WebSocket 支持分片文本消息，总消息上限 1 MiB。

## 安全纪律

1. `Readonly`、危险性、参数、执行站点和 MCP 许可只在生产 `CommandDescriptor` 中声明。
2. UI、Help、Web、MCP 与命令手册都从最终 `CommandRegistry` 投影，不复制名单。
3. 危险操作必须由宿主确认；`--yes`、HTTP 参数或 MCP 参数都不能绕过远程确认。
4. token、密码、私钥和连接串不得写入命令结果、日志或审计文件。
5. 3.2.2 的正式模块只来自显式 Z manifest；发布侧先生成候选和完整 SHA-256，再原子部署到各项目 Z 目录，运行期不重复计算校验和。
   3.1.9 是旧名 AppShell 的最后快照；3.1.8 不作为稳定支持版本。
   双进程服务历史设计不随消费包发布。

## 最小验收

- `vulcan.module.list` 能显示名称、版本、槽和命令数；坏 DLL 或缺依赖只影响对应模块。
- 模块命令在 Help、命令目录与 MCP schema 中保持同源；服务端本地模块热重载后会更新本地权威目录。
  Shell 前端在连接后动态注册的命令需要重连，才会重新发布到服务端权威目录。
- 独立宿主的真实 `tools/list` 能看到策略允许的 Diana、Janus、Mercury、Minerva 工具；调用、卸载与重载
  共用后台命令总线，网关端口不变。
- 前端连接后目录自动出现；多前端歧义、定向、中断和离线执行行为符合上述规则。
- MCP/Web 默认端口在同机多应用间稳定分离，冲突顺延有界，Stop 后端口可重新绑定。
