# AppShell 3.0 冻结前代码审查与整改清单

| 项 | 值 |
|---|---|
| 审查对象 | `b-Code-AppShell/src` 与 `b-Code-AppShell/tests` 全量（约 15000 行） |
| 审查范围除外 | `DockingHost` 的中央工作区布局逻辑（另见[中央主工作区体验改造](AppShell_3.0_中央主工作区体验改造.md)） |
| 目标版本 | `3.0.0` |
| 首次审查日期 | 2026-07-29 |
| 当前状态 | 复审通过，可放行冻结 |

本文件是冻结放行的前置检查单。每条编号 `FZR-xx` 为整改单元，提交信息请引用编号。
整改完成后按末尾的《复审移交清单》回填，再提交复审。

## 结论

工程基线扎实，不存在需要推翻的结构性问题：

- Release 构建 0 警告 0 错误，仓库根 `Directory.Build.props` 已开启 `TreatWarningsAsErrors`，且全仓无 `#pragma warning disable` 与 `SuppressMessage`。
- 四个包的 `PublicAPI.Shipped.txt` 均已填充（Core 496 行 / Services 343 行 / Shell 324 行 / ServiceHost 47 行），`Unshipped` 为空。API 冻结是有实际编译期约束的。
- 安全缺省方向正确：无确认通道即拒绝执行、非回环强制 TLS、令牌用 `CryptographicOperations.FixedTimeEquals` 比较、工作区路径边界在实现层统一校验。
- 初审时单元测试 19 个全部通过；中央工作区整改后为 20/20，本清单首轮整改后为 45/45，复审与最终安全/停靠回归补齐后为 69/69。

但按冻结标准，**5 项阻塞问题必须在打标签前修复**，其中 FZR-01 属于权限提升。另有 8 项建议、8 项记录，以及一处需要与现行《冻结体检与已知限制》对齐的口径冲突。

## 复核基线

以下证据在 2026-07-29 于本机取得，整改后复审需重新取证：

```powershell
$env:DOTNET_EnableWriteXorExecute='0'
dotnet build .\b-Code-AppShell\AppShell.sln -c Release --nologo
# 已成功生成。0 个警告，0 个错误

dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --nologo
# 已通过! - 失败: 0，通过: 19，已跳过: 0
```

## 阻塞项（冻结前必须修复）

| 编号 | 问题 | 类别 | 状态 |
|---|---|---|---|
| FZR-01 | 令牌明文进日志并广播给全部客户端 | 安全·权限提升 | 已修复（写路径由整改闭合，读路径由复审补齐，见 FZR-01R） |
| FZR-02 | Web 会话 ID 含冒号导致回源解析错误 | 功能 | 已修复，复审验证通过 |
| FZR-03 | 远程工作区在 UI 线程同步阻塞 HTTP | 可用性 | 已修复 |
| FZR-04 | `svc.restart` 与单实例互斥体竞态 | 功能 | 已修复 |
| FZR-05 | 前端事件循环被大消息与坏 JSON 打死 | 健壮性 | 已修复 |

### FZR-01 令牌明文进日志并广播给全部客户端

**证据**

- `src/AppShell.Core/Commands/CommandBus.cs:124` `RedactSensitiveArguments` 的正则为 `(\b(?:code|token|password|passwd|secret)\s*=\s*)(...)`，只匹配敏感词**紧跟等号**的形式。
- 框架自身设置令牌的两条路径都不匹配：
  - `app.set key=mcp.token value=SECRET` —— `token` 后面是空格加 `value`，不是等号；
  - `web.token SECRET` —— `WebCommands.SettingCommand` 的 `value` 参数声明了 `Position = 0`，位置参数形式根本没有等号。
- `src/AppShell.Services/Web/WebGateway.cs:942` `OnLogEntry` 遍历 `_clients.Values` 无差别推送每一条日志，不判断会话 scope。

**影响**

命令回显以明文写入滚动日志文件，并实时推送给所有已连接 WebSocket 客户端。一个仅有 `read` scope 的已配对设备，只要保持连接，即可在管理员执行 `app.set key=web.token value=…` 的瞬间收到新令牌明文，据此提升到 `operate` / `admin`。这是从最低权限到最高权限的完整提升链。

**修复要求**

两处都要改，缺一不可：

1. 脱敏覆盖 `value=` 与位置参数形式。建议不再靠文本猜测，改为按命令名与参数名的敏感登记表脱敏（`web.token`、`app.set key=*.token` 等），文本正则仅作兜底。
2. 日志广播按 scope 过滤：`read` scope 不得收到 `cmd:*` 类别的回显，或统一只推送已脱敏文本。

**验收**

新增单元测试覆盖三种写法（`token=`、`key=x.token value=`、位置参数）均不出现明文；新增测试断言 `read` scope 会话收不到 `cmd:` 类别事件。

### FZR-02 Web 会话 ID 含冒号导致回源解析错误

**证据**

- `src/AppShell.Services/Web/WebGateway.cs:854` `SessionSource` 拼为 `{Kind}:{Id}:{Name}`。
- 同文件 `:857` `SessionIdFromSource` 用 `Split(':', 3)` 取 `parts[1]`。
- 同文件 `:725` `CreateSession`：客户端未提供 `X-Session-Id` 时，自动生成的 ID 形如 `Web:127.0.0.1:Web`，**自身含两个冒号**。

于是 `SessionSource` 产出 `Web:Web:127.0.0.1:Web:Web`，反解只得到 `"Web"`，`_clients` 查表必然失败。

**影响**

不发送 `X-Session-Id` 的客户端（浏览器端；桌面 `ShellServiceClient` 始终发送 32 位十六进制 ID，故自测覆盖不到）永远无法完成二次确认：`RequestWebConfirmation` 首次查表即返回 false，`/api/confirm` 恒返回 404。失败方向是安全的（拒绝），但功能是坏的，且只在非桌面客户端上坏。

**修复要求**

会话来源标签改用不含分隔符的编码（推荐对 ID 做定长哈希或使用不会出现在 ID 中的分隔符），或自动生成的 ID 本身禁止含冒号。两端必须成对修改，并保证 `SessionSource` → `SessionIdFromSource` 往返恒等。

**验收**

新增往返测试：对含冒号 ID、含中文名、空名等边界输入，`SessionIdFromSource(SessionSource(s)) == s.Id` 恒成立。

### FZR-03 远程工作区在 UI 线程同步阻塞 HTTP

**证据**

- `src/AppShell.Services/Web/RemoteWorkspaceService.cs:67` `Execute` 使用 `.GetAwaiter().GetResult()` 同步等待。
- 调用方 `src/AppShell.Shell/Resource/ResourceView.xaml.cs:65` `ReloadTree` 运行在 UI 线程（`Loaded` 事件与 300 ms 防抖 `DispatcherTimer` 均在 UI 线程触发）。
- `ShellServiceClient` 的 `HttpClient.Timeout` 为 30 秒。

**影响**

服务端一慢，整个界面最长冻结 30 秒。期间 MCP 宿主确认对话框走 `owner.Dispatcher.Invoke`（`src/AppShell.Shell/Mcp/RemoteConfirmDialog.cs:16`），会一并卡死，远程危险指令确认因此超时被拒。

**修复要求**

`IWorkspaceService` 是同步接口，属冻结公开面，本轮不改签名。最小修法：

1. 远程实现单独设置短超时（建议 5 秒）并可配置，避免 UI 长时间无响应；
2. 阻塞期间禁用资源树刷新入口，失败时在树上给出明确错误态而非静默。

若判断需要异步化接口，应作为 3.1 的 API 演进项单列，不在 3.0 冻结内做。

**验收**

以不可达服务端启动客户端模式，资源窗口在 5 秒内返回错误态，期间主窗口菜单可正常点开。

### FZR-04 `svc.restart` 与单实例互斥体竞态

**证据**

- `src/AppShell.ServiceHost/ServiceCommands.cs:52` 先 `Process.Start(start)`，随后 `Application.Current.Dispatcher.BeginInvoke(requestStop)`。
- `src/AppShell.ServiceHost/ServiceHost.cs:20` 进程入口即以 `initiallyOwned: true` 抢占 `Local\<ServiceName>.ServiceHost`，抢不到直接 `return 2`。
- 旧进程的 `mutex.ReleaseMutex()` 在 `app.Run()` 返回后的 `finally` 中才执行。

**影响**

新进程几乎必然在旧进程释放互斥体之前完成入口检查，以退出码 2 静默退出；随后旧进程正常关闭。`svc.restart` 的实际效果是**服务彻底停止**，且没有任何错误提示。

**修复要求**

改为先停后启，或让新进程在入口对互斥体做有限等待（建议等待上限 10 秒后再判定为重复实例）。若采用等待方案，需确认它不会让"用户误双击启动第二实例"的场景产生 10 秒无响应窗口。

**验收**

在已运行的服务上执行 `svc.restart`，确认新进程存活、`svc.status` 可用、端口重新监听。

### FZR-05 前端事件循环被大消息与坏 JSON 打死

**证据**

- `src/AppShell.Services/Web/ShellServiceClient.cs:312`：`if (result.MessageType != Text || !result.EndOfMessage) continue;` 丢弃全部分片消息，无日志。缓冲区为 64 KB。
- 同文件 `:315` `JsonDocument.Parse` 无 try/catch，而 `:268-277` 的外层只捕获 `OperationCanceledException`、`WebSocketException`、`HttpRequestException`。
- 对照：服务端 `WebGateway.ReceiveTextMessageAsync`（`WebGateway.cs:704`）正确做了分片重组并有 1 MiB 上限，两侧不对称。

**影响**

超过 64 KB 的 `uiCommand` 被静默丢弃，服务端只能等到 15 秒中继超时，无任何诊断线索。一条畸形 JSON 会让 `JsonException` 穿透 `RunEventLoopCoreAsync` 逃逸，重连循环终止，前端从此静默失联，且异常成为未观测任务异常。

**修复要求**

1. 客户端复用与服务端同构的分片重组逻辑并设同样的 1 MiB 上限（建议提取为共享实现，避免两份）;
2. `JsonDocument.Parse` 包裹异常处理，坏帧记日志后跳过而非终止循环；
3. `RunEventLoopCoreAsync` 增加兜底 `catch`，确保任何异常都回到重连节奏而非杀死循环。

**验收**

新增测试：发送 100 KB 的 `uiCommand` 能被正确执行并回执；发送畸形 JSON 后事件循环仍能处理后续正常消息。

## 建议项（应修，可评估后带病冻结）

| 编号 | 问题 | 位置 | 状态 |
|---|---|---|---|
| FZR-06 | 菜单重建抛异常导致菜单栏半残 | `ShellWindow.xaml.cs` | 已修复 |
| FZR-07 | 设置文件非原子写，损坏即静默清空 | `SettingsService.cs` | 已修复 |
| FZR-08 | 请求体先全量读入再判大小 | `HttpRequestBodyReader.cs` | 已修复 |
| FZR-09 | 限流在鉴权之后，令牌可无限枚举 | `WebGateway.cs` | 已修复 |
| FZR-10 | 日志广播无背压 | `WebGateway.cs` | 已修复 |
| FZR-11 | `McpExposurePolicy` 为全局可变静态且非线程安全 | `McpExposurePolicy.cs` | 线程安全已修；多实例隔离转 3.1 |
| FZR-12 | 代理描述符创建逻辑存在两份且行为不一致 | `FrontendCommandCatalog.cs` | 已修复 |
| FZR-13 | 冻结契约核心类零单元测试 | `tests/AppShell.Tests` | 已修复 |

**FZR-06**：`BuildMenus` 先 `MainMenu.Items.Clear()` 再逐项 `EnsureMenuCommandValid`，校验失败直接抛 `InvalidOperationException`。该方法由 `WindowsChanged` 触发（`ShellWindow.xaml.cs:126`），而模块卸载会经 `DockingHost.UnregisterWindow` 触发该事件。派生应用只要在 `ToolMenuActions` 中引用模块指令，模块一下线就得到半空菜单栏加错误弹窗。构造期 fail-fast 是正确设计，重建期应降级为禁用菜单项。

**FZR-07**：`Persist` 直接 `WriteAllText`，写入中途崩溃即损坏；而构造函数捕获异常后**静默以空配置起步**，用户全部设置无声蒸发。同仓库 `PromptGovernanceStore.Save`（`PromptGovernanceStore.cs:351`）已采用临时文件加 `File.Move` 的正确写法，直接对齐即可。另注意 `Persist` 只捕获 `IOException`，`UnauthorizedAccessException` 会穿透 `Set` 抛给调用方。

**FZR-08**：`McpGateway` 的 1 MiB 检查发生在 `ReadToEndAsync()` **之后**，`WebGateway.ReadJsonAsync` 则完全没有上限。回环服务风险有限，但 `web.bind` 一旦对局域网开放即成内存耗尽入口。应在读取时按上限截断。

**FZR-09**：`AllowRequest` 在 `Authenticate` 之后调用，401 路径完全不计数，`web.token` 可被无限次暴力枚举；且限流键取自客户端可自选的 `X-Session-Id`，换头即重置。建议对未通过鉴权的请求按远端地址限流。

**FZR-10**：每条日志对每个客户端起一个 `SendIgnoringErrorsAsync`，无队列上限。这与 `ShellLog` 注释承诺的 N-03 承压目标（1000 条/秒不阻塞调用方）相矛盾——`debug.logflood` 期间若有 WS 客户端连接，待发送任务会无界堆积。

**FZR-11**：`ReadonlyCommands` 是无锁 `HashSet`，`RegisterReadonly` 写入与网关线程读取并发；`ModuleOfCommand` / `ModuleExposure` 两个静态委托使同进程多实例互相污染（测试中已是如此）。该类注释本身在批判"一件事实两处声明"，其自身形状值得在 3.1 收口。

**FZR-12**：`FrontendCommandCatalog.CreateProxy` 不设 `ConfirmPrompt`，`FrontendCommandCapability.CreateProxy` 则设。当前不产生错误行为（确认最终在前端本地发生），但两份实现正是 `McpExposurePolicy` 注释所警惕的重复声明的复活形态，应合并为一处。

**FZR-13**：初审时现有 19 个测试集中在停靠契约、前端目录与端口派生。而冻结契约的真正核心——`CommandParser` 的转义与引号规则、`CommandBus` 的参数绑定与确认闸口、`WorkspaceService` 的路径越界防护、`PromptGovernanceStore` 的提案状态机——**零单元测试**。这几处恰是"冻结后不得变更行为"的地方，也是 FZR-01、FZR-02 的藏身之处。建议冻结前补齐这四个类的测试，为冻结契约留下可回归的定义。

## 记录项（登记即可，不阻塞）

| 编号 | 问题 | 位置 | 去向 |
|---|---|---|---|
| FZR-14 | `WriteEntry` 出错时把 `_writer` 置 null 却不 `Dispose`，句柄泄漏且文件被永久锁住 | `ShellLog.cs` | 已修复 |
| FZR-15 | `_debounce ??=` 在多线程文件事件下有竞态，可能创建并泄漏多余定时器 | `ModuleHost.cs` | 已修复 |
| FZR-16 | `UiContext == null` 的提前返回会漏掉刚创建的 ALC，退出路径下泄漏 | `ModuleHost.cs` | 已修复 |
| FZR-17 | 每次热重载执行两次阻塞式 GC | `ModuleHost.cs` | 已修复 |
| FZR-18 | 端口重试成功后持久化的是**请求端口**而非实际绑定端口；`Stop()` 消息中的 `Port` 未复位 | `WebGateway.cs`、`McpGateway.cs` | 已修复 |
| FZR-19 | MCP 留痕 jsonl 无轮转与体积上限 | `McpAuditRecorder.cs` | 3.1：审计归档策略；3.0 明示不静默淘汰 |
| FZR-20 | `CommandManualGenerator` 直接取 `AppIdentity.Current` 而非注入身份，与 `McpGateway` 的注入式写法不一致 | `CommandManualGenerator.cs` | 3.1：随身份注入 API 演进 |
| FZR-21 | `App.xaml.cs` 的 `Stub()` 为死代码；`--exec` 作为末位参数会被静默忽略 | `App.xaml.cs` | 已修复 |

## 整改回填（2026-07-29）

- **FZR-01**：`CommandBus` 先解析命令再按命令名、设置键和参数名收集敏感值；`token=`、
  `app.set key=*.token value=`、`web.token <位置参数>` 的命令回显与结果回显均脱敏。Web 日志广播拒绝把
  所有 `cmd:*` 类别发送给只有 `read` scope 的会话。
- **FZR-02**：来源标签使用 `v1.<base64url(sessionId)>`，显示名留在独立尾段；自动 Web 会话 ID 改为
  远端地址、客户端种类和名称的稳定 SHA-256 截断值。含冒号 ID、中文名和空名均可逆。
- **FZR-03**：`RemoteWorkspaceService` 使用 `ShellEndpointProfile.ConnectTimeout`，默认 5 秒；资源根与懒加载
  子目录均在后台读取，刷新期间入口禁用，超时或失败在树中显示明确错误项，UI Dispatcher 不再同步等待 HTTP。
- **FZR-04**：ServiceHost 第二进程对同名单实例互斥体有限等待 10 秒；`svc.restart` 启动的后继进程会等待
  前驱退出释放互斥体再接管。普通误双击没有可见窗口冻结，超时仍以退出码 2 拒绝重复实例。
- **FZR-05**：服务端与前端共用 `WebSocketMessageReader`，支持分片、忽略非文本帧并执行 1 MiB 上限；前端
  对坏 JSON 记录 Trace 后继续读取，事件循环任何非取消异常都会回到 500 ms 重连节奏。
- **FZR-06～10、12**：菜单采用完整构造后原子替换；动态失效项降级为禁用；设置采用临时文件同卷替换并保留
  损坏原件；HTTP 请求体边读边限 1 MiB；失败鉴权尝试按远端地址计数并在鉴权前检查额度，成功请求按会话
  限流；每客户端日志队列容量 256、满时丢最旧；两个前端代理工厂统一经
  `FrontendCommandCapability.CreateProxy()`。
- **FZR-11**：只读兜底集合改为并发字典，两个委托改为原子读写，解决审查指出的数据竞争。把 process-global
  静态策略改成实例策略会改变 3.0 已冻结装配面，登记为 3.1 演进项；3.0 不支持同进程内多套不同 MCP 模块策略。
- **FZR-13**：新增 `CommandParser` 引号/转义、`CommandBus` 绑定/确认、`WorkspaceService` 越界、
  `PromptGovernanceStore` 提案状态机与持久化测试；连同五个阻塞项和建议项回归，框架用例由 20 增至 45。
- **FZR-14～18、21**：失败日志流先释放再重建；模块防抖创建串行化，退出早退卸载新 ALC，移除每次重载的
  强制 GC；MCP/Web 持久化实际绑定端口且 Stop 后 `Port=0`；删除 Demo 死代码并显式告警末位空 `--exec`。
- **FZR-19、20**：不在 3.0 以静默删除审计记录换取固定体积，也不为身份注入临时增加冻结公开重载；均转入
  3.1，消费限制见《运行时约束与已知限制》。

自动化用例集中在 `FreezeBlockerTests`、`CoreFreezeContractTests`、`QualityRemediationTests`、
`FrontendCommandCatalogTests`、`McpGatewayPortTests`、`McpSecurityTests` 和 `DockingContractTests`。Debug 当前 69/69 PASS；Release 与完整候选流水线结果
在《冻结执行证据》中统一回填。

## 复审记录（2026-07-29）

复审逐条比对了代码实态，不以回填自评为准。**19 项验证通过，1 项未闭合并已在复审中补齐**。

### FZR-01R 读路径补齐（复审新增并已修复）

整改把三种**写入**形式与日志广播闭合了，但同一条提权链经**读取**路径仍然打开：

- `app.get` 在 `BuiltinCommandDefinitions` 中声明 `Readonly: true`；
- `WebGateway.CanExecute` 对 `scope=read` 的设备按 `descriptor.Readonly` 直接放行；
- `CommandBus.ExecuteAsync` 的脱敏只作用于 `_log.Log` 的回显文本，`return result` 返回的 `CommandResult.Message` 未脱敏，而它被 `WriteJsonAsync(context, result, 200)` 原样序列化进 HTTP 响应体；
- `app.get` 未被 MCP 硬排除，`Readonly=true` 使其在默认 `readonly` 策略下即为可见可调用的工具，结果文本同样进入 `tools/call` 载荷。

因此 `read` scope 设备或任意已接入的本地 MCP 客户端，可用一次 `app.get key=web.token`（或无参 `app.get` 全量倒出）取得 LAN bearer token 并提权。

**修复**：脱敏下沉到指令结果本身。`BuiltinCommands` 的 `app.get` 与 `app.set` 对 `*.token` / `*.password` / `*.passwd` / `*.secret` 结尾的键一律回报 `(已配置)`，日志、HTTP 响应、MCP 载荷与控制台一次性闭合；非敏感键行为不变。

**回归**：`FreezeBlockerTests.SecretSettingValuesAreMaskedInCommandResultsNotOnlyInLogs` 断言落在 `CommandResult.Message` 而非日志上，并以 `Assert.Contains("(已配置)")` 作为正向守卫——掩码一旦被移除该断言立即失败；同时断言 `console.history = 500` 保持明文，确保掩码是选择性的。

**根因备注**：本条并非整改遗漏，而是初审验收标准只覆盖了写入形式。验收标准按"命令形态"枚举而非按"敏感值的全部出口"推导，是这次的方法论教训。

### 其余各项复审结论

| 编号 | 复审结果 |
|---|---|
| FZR-02 | 通过。`v1.<base64url>` 编码使 ID 中的冒号不再破坏解析，往返恒等成立，并保留旧格式兼容分支 |
| FZR-03 | 通过。`Task.Run` 将阻塞调用移出 UI 线程，刷新期禁用入口并显示错误节点，远程实现 5 秒超时 |
| FZR-04 | 通过。`initiallyOwned: false` + 10 秒等待，`AbandonedMutexException` 视为获取成功；`composition.Dispose()` 先于 `ReleaseMutex()`，端口在新进程接管前已释放 |
| FZR-05 | 通过。`WebSocketMessageReader` 两侧共用，含分片重组、1 MiB 上限与分片类型一致性校验；坏 JSON 跳过，事件循环有兜底 catch |
| FZR-06~10、12 | 通过。菜单原子替换且失效项降级禁用；设置临时文件替换并回滚内存值；请求体边读边限；限流前置到鉴权之前按远端地址计数；日志改 `BoundedChannel` + `DropOldest`；两个代理工厂已合一 |
| FZR-11 | 部分修复，取舍合理。`ConcurrentDictionary` + `Volatile` 消除数据竞争；实例化改造会动已冻结装配面，转 3.1 正确 |
| FZR-13 | 通过。四个核心类的测试均已补齐 |
| FZR-14~18、21 | 通过。逐条核对代码属实 |
| FZR-19、20 | 转 3.1，理由成立，与已知限制第 1 条自洽 |

### 复审新增登记项

| 编号 | 问题 | 去向 |
|---|---|---|
| FZR-22 | 敏感设置键谓词在 `CommandBus` 与 `BuiltinCommands` 各存一份。冻结期不宜为共享它新增公开 API 或引入仓库中尚不存在的 `InternalsVisibleTo` 机制，故暂留副本 | 3.1：收敛到单一真值 |
| FZR-23 | `RedactSensitiveArguments` 改为从解析结果重建回显文本，命令名转小写、键值参数按字典枚举序输出，回显不再是用户字面输入。功能无影响，但"回显"属 C-01/C-02 契约 | 3.0：在《API 与指令手册》补充说明 |

### 复审证据

```powershell
dotnet build .\b-Code-AppShell\AppShell.sln -c Release --nologo
# 已成功生成。0 个警告，0 个错误

dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --nologo
# 已通过! - 失败: 0，通过: 46，已跳过: 0
```

四个 `PublicAPI.Unshipped.txt` 均只有 `#nullable enable` 基线，读路径补齐未新增公开面。

### 冻结前终核补充（2026-07-29）

- MCP Bearer 改为大小写不敏感解析 scheme 后使用 `CryptographicOperations.FixedTimeEquals` 比较 token；
- `initialize` 的客户端名与协议文本在进入会话缓存/日志前去控制字符并限长，工具名也在查找和回显前校验；
- `tools/call` 的协议拒绝、早退、确认回调异常和执行异常均只写一条脱敏审计，异常响应与日志只含异常类型；
- 文件审计使用 `session.Id/session.Name` 作为客户端身份，并限制 Client 256、Tool 128、Arguments 500、Result 100 字符；
- `app.get key=code` 与全量设置列表不再返回明文；命令历史只持久化已脱敏回显，旧无头历史在 3.0 首启清空。

本轮 Debug/Release 均为 0 warning / 0 error、69/69 PASS；四份 `PublicAPI.Unshipped.txt` 仍只有
`#nullable enable`，公共 API 基线行数未变化。

## 与现行《冻结体检与已知限制》的口径冲突

[运行时约束与已知限制](../package/AppShell_3.0_运行时约束与已知限制.md)是对外消费文档，冻结后不得由消费方掩盖。本次审查发现两处表述与代码实态不符，**须随整改同步订正**，否则会误导消费方：

1. **已解决**：Web 日志广播已改为有界队列；`McpAuditRecorder` 作为不可静默淘汰的审计资产已在对外限制中单列。
2. **已解决**：远程工作区读取已移出 UI Dispatcher，并使用默认 5 秒的可配置连接超时；线程亲和表述已补充该边界。

## 复审移交清单

整改完成后请逐项回填，连同新的构建与测试证据一并提交复审：

- [x] FZR-01 至 FZR-05 全部修复，且各自的验收项已有自动化测试覆盖（FZR-01 的读路径由复审补齐，见 FZR-01R）
- [x] FZR-06 至 FZR-13 逐条给出"已修复"或"带病冻结 + 理由 + 计划版本"的结论
- [x] FZR-14 至 FZR-21 已登记去向（修复或转入 3.1 待办）
- [x] 《冻结体检与已知限制》两处口径冲突已订正
- [x] `dotnet build -c Release` 仍为 0 警告 0 错误
- [x] `dotnet test` 全绿，Debug/Release 69/69（含复审补齐与最终安全/停靠回归）
- [x] 四个 `PublicAPI.Unshipped.txt` 仍只有 `#nullable enable` 基线，无新增公开面
- [x] 复审已逐条比对代码实态，结论见《复审记录》
- [ ] Debug 配置与完整候选流水线结果回填至《冻结执行证据》（由发布执行人完成）
