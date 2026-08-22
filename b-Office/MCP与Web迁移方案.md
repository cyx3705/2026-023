# MCP 与 Web 迁移方案

2026-08-21。配套《模块边界整治备忘》的 B 项。本文的任务之一是**决定 Web 迁不迁**。

---

## 一、现在的账

删完死代码之后（宿主 4.2.0）：

| 项目 | 行数 |
|---|---|
| HistoryVulcan.Services | 6,559 |
| HistoryVulcan.Core | 2,394 |
| HistoryVulcan.ServiceHost | 1,419 |
| HistoryVulcan.Extensibility | 720 |
| **合计** | **11,092** |

两个候选子系统：

| 子系统 | 行数 | 占比 |
|---|---|---|
| MCP | 2,841 | 26% |
| Web | 840 | 7.6% |

---

## 二、决定：Web **迁出**（2026-08-21 改；本节推翻了本文初稿）

### 初稿写的是"不迁出"，理由是错的

初稿说：Web 是「所有模块都坏掉时」的恢复传输，所以必须留在宿主。

**这句话把传输和恢复路径当成了一回事。** 真正的恢复路径是文件系统：

```
把好包拷进 %APPDATA%\HistoryVulcan\Modules\<模块>
  → ModuleDirectoryWatcher 触发（ModuleHost.EnableFileWatching 默认 true）
  → 宿主自己重载
```

**全程不需要任何传输。** 2026-08-21 实测：拷完文件宿主自己就重载了，没调过
`vulcan.module.reload`。所以"所有模块都坏掉还能自救"这个属性，靠的是文件监视，
不是 `/api/command`。

### 声明式暴露是让它能走的那把钥匙

命令自己声明上哪些面之后，Web 网关就从"宿主特权设施"降级成**一个按声明取命令的消费方**——
和 MCP 是同一类东西。同类东西应该同样对待：都能是模块。

### 搬出去损失什么

| 传输不可用时 | 降级到 |
|---|---|
| `module.list` 查活状态 | 读日志 |
| `module.unload` 解文件锁 | 停服 / 起服 |
| 驱动活界面（`aurora.ui.*`） | 界面本来就没了才会遇到 |

装模块、重载、跑门禁、开工作区、合并——**一件都不受影响**。

### 必须配套的两件事

1. **`--install-module <path>` 离线开关**（备忘录已列）。宿主服务起不来时，
   文件监视也不在，这一格必须由 CLI 补上。
2. **CLI 的通道要想清楚**。CLI 是客户端型，它得跟活进程说话。Web 成了模块之后，
   CLI 就是跟那个模块说话——正常情况下没问题，Web 模块坏了就回退到文件路径。
   这一条在第 4 步（CLI 骨架）落地时定，不在本文展开。
## 三、决定：MCP 也迁出，与 Web 合成一个模块 **HistoryPortunus**

Portunus——罗马的钥匙、门户、港口之神。

选它的理由不只是"门"：**门搬走，锁留下**。`McpExposurePolicy` 决定 agent 能调哪些指令，
那是安全边界，必须留在宿主；否则换一个模块就能给自己放权。这个切法正好对上这个名字。

（备选 **HistoryTerminus**，界石之神；但 Terminus 在英文里像"终端/终点"，容易误读。）

### 切法：2,560 走，281 留

**搬进 HistoryPortunus：**

| 文件 | 行数 |
|---|---|
| `Services/Mcp/McpGateway.cs` | 882 |
| `Services/Mcp/CommandCatalogCommands.cs` | 413 |
| `Services/Mcp/PromptGovernanceStore.cs` | 396 |
| `Services/Mcp/McpCommands.cs` | 289 |
| `Extensibility/Mcp/CommandSchemaExporter.cs` | 216 |
| `Services/Mcp/McpGateway.Protocol.cs` | 157 |
| `Extensibility/Mcp/CommandManualGenerator.cs` | 121 |
| `Services/Mcp/McpAuditRecorder.cs` | 75 |
| `Services/Mcp/IEffectivePromptDescriptionReader.cs` | 11 |

**留在宿主：**

| 文件 | 行数 | 为什么留 |
|---|---|---|
| `Core/Mcp/McpExposurePolicy.cs` | 159 | 安全边界。模块不能决定自己能被调什么 |
| `Core/Mcp/McpConfirmationScope.cs` | 51 | 确认语义属于总线，不属于某条传输 |
| `Core/Mcp/PromptTextIntegrity.cs` | 46 | 提示词完整性校验，同上 |
| `Core/Mcp/IMcpAuditLog.cs` | 25 | 契约；实现搬走，接口留下 |

**宿主 11,092 → ~8,200 行**（含 Web 瘦身）。

### Web 那一半怎么切

| 文件 | 行数 |
|---|---|
| `Services/Web/WebGateway.cs` | 513 |
| `Services/Web/WebGateway.Protocol.cs` | 202 |
| `Services/Web/ShellEndpointProfile.cs` | 52 |
| `Services/Web/WebSocketMessageReader.cs` | 39 |
| `Services/Web/HttpRequestBodyReader.cs` | 34 |
| **小计** | **840** |

其中 socket 半边（`/api/events`、`EventClient`、`_clients`、`_pending`、日志广播、
`PublishModuleRevision`、`CompletePendingForSession`）**已经全死**——进程外前端删掉之后
零消费者，`_pending` 只读不写。**先删再搬，别把尸体一起搬过去**，预计 840 → ~300。

`Core/Clients/ClientSession.cs`（68 行）是两个网关共用的会话契约，**留在 Core**。

### 一个模块还是两个

**建议一个。** 两个网关共享的东西很多：回环绑定、令牌鉴权、会话身份、按声明过滤、
对总线执行。拆成两个模块要付两份 manifest / 版本 / 发布，换来的隔离却是假的——
它们编译到同一份宿主快照，宿主 API 一动一起坏（关联失效）。

Portunus 是港口与门户之神，一个港口停两条航线，名字上也对得住。

### 顺带解决备忘录第 2 项

`vulcan.command.manual` 原计划给 Diana。既然 `CommandManualGenerator` 本来就是从 MCP
投影生成手册的，它应该跟 MCP 走，归 Portunus。**备忘录第 2 项作废，并入本方案。**

---

## 四、顺序

```
1. [x] 删死代码                          宿主 4.2.0
2. [x] Web 瘦身：删已死的 socket 半边      840 → 532，网关不再持有按客户端状态
3. [x] Program.Main 未知参数报错           宿主 4.5.0，与 5/6 同轮做（见下）
4a.[x] Web 迁出成 HistoryPortunus 0.2.0   宿主 4.3.0，见下「第一轮实况」
4b.[x] MCP 迁出，并入同一模块              宿主 4.4.0，见下「第二轮实况」
5. [x] --install-module 离线开关           宿主 4.5.0
6. [x] CLI 骨架 + 它自己的暴露声明          宿主 4.5.0，只声明开发管线那几条
7. [x] 开发路线从 Diana 搬回宿主            宿主 4.6.0 / Diana 2.0.0
```

### 第一轮实况（2026-08-21，Web 迁出）

**多出来一个前置条件，方案里没写：宿主必须在拆除阶段 Dispose 模块实例。**

`ModuleHost` 此前只对界面模块调 `IUiModule.DestroyUi()`，**非界面模块没有任何拆除回调**。
Portunus 是 `ui: false`，它的 `HttpListener` 会被直接遗弃。后果不是"少个优化"：

- 旧监听器继续占着端口，新实例只能退到下一个端口 → 每重载一次 endpoint 端口涨一格
- 旧监听器仍持有活的 `CommandBus` 引用与一枚有效令牌
  → **每重载一次，就多一个仍能执行任意指令的入口**
- 监听线程钉住旧 ALC，可回收上下文再也卸不掉

因此 4.3.0 在 `TeardownSnapshot` 与 `ModuleHost.Dispose` 中回收实现 `IDisposable` 的模块实例，
次序是「界面先拆 → 回收实例 → 卸载 ALC」。Portunus 的 `MinimumHistoryVulcanVersion` 因此是 4.3.0。

**这条契约缺口对第 2 轮同样致命**——MCP 网关也持有端口。

#### 一个真实的代价：触发重载的那条指令拿不到响应

实测：经 Portunus 发 `vulcan.module.reload`，客户端拿到的是 `基础连接已经关闭`。
指令执行成功了，但服务它的监听器在响应写回之前就被拆掉了。

MCP 在宿主里时没有这个问题（宿主不随重载重启）。**搬出去之后这是结构性的，不是缺陷**：
传输住在被重载的那个快照里，就一定会跟着断。四轮重载实测的行为是：

| | |
|---|---|
| 端口 | 稳定 8963，不漂移 |
| 遗留监听器 | 0（8964+ 全空） |
| 令牌 | 每轮换发 |
| 断开窗口 | 约 1 秒（旧实例已拆、新实例未起，连接被拒） |

**客户端契约因此变成：每次调用前重读 `endpoint.json`，并对重载类指令预期一次断连后重试。**

第 2 轮要特别当心：MCP 是 agent 的**有状态会话**，断开不是重试一次那么轻。
`diana.trial.load` 这类装模块的指令会让 agent 的 MCP 工具集体掉线一秒。
搬 MCP 之前应当先想清楚这一条能不能接受，或者是否需要别的办法。

#### 顺带删掉的死码

`ShellEndpointProfile.cs` + `ShellConnectionState`（52 行，全仓零引用，进程外前端的尸体）、
`IsTrustedLoopbackShell`、`SessionIdFromSource`、`Base64UrlDecode`——都没搬，直接删。
公开面按 `*REMOVED*` 约定注销 60 条（Services 56 + ServiceHost 4）。

#### 未搬的两个传输层零件

`LoopbackHttpTransport`（115 行）与 `HttpRequestBodyReader`（34 行）在 Portunus 里有一份副本，
宿主里保留原件——因为 `McpGateway` 还在用，而它们是 `internal`，模块够不着。
提公开面等于为即将删除的类型永久扩大契约。**第 2 轮搬完 MCP 后删除宿主那一份。**

### 声明式暴露的范围（2026-08-21 澄清）

**它只服务于新 CLI，不是一套覆盖 web/mcp 的通用治理。**

初稿把它写成了「命令声明自己上 Cli / Mcp / Web 哪些面」，并据此断言它必须排在
Portunus 之前。**两条都错了：**

- 两个网关搬出去之后**照旧服务今天服务的那些**——MCP 按 `McpExposurePolicy` 过滤，
  Web 全开。搬家不需要任何新声明，所以 Portunus 不依赖它。
- 「缺省全开还是全关」那个两难，只在"给 163 条现有命令补声明"的前提下存在。
  范围收到 CLI 之后**缺省当然是全关**：CLI 是新面，声明哪条才有哪条，
  不声明的命令维持现状、不会从任何地方消失。

因此它跟着 CLI 走（第 6 步），不单列一步。

**web 与 mcp 的暴露治理另开一轮**，前提是它们已经分离出去、各自有明确归属。
在那之前谈"谁该上哪个面"没有落点。
## 五、前置条件与风险

### 必须同时立的门禁

**每个 MCP 工具名必须能反查到一条已注册指令。**

今天 MCP 造不出能力，是因为它是宿主里的一段投影代码——这是实现方式偶然带来的保证。
**一旦它变成模块，没有任何结构性的东西阻止那个模块注册 MCP-only 的工具。**
那样"MCP 只是投影"就从事实退化成惯例。

这条门禁现在写是免费的（当前 100% 成立），等违反了再写就得先清账。

### 需要一起想的几件事

| 事项 | 说明 |
|---|---|
| 指令改名 | `vulcan.mcp.*` 6 条 → `portunus.mcp.*`；`mcp.autostart` 的持久化键要迁 |
| 端口与自启 | MCP 网关自己监听端口。模块监听端口没问题，但"随宿主自动监听"的开关归谁要定 |
| 装载失败 | Portunus 装不上就没有 MCP。可接受——那正是 Web 通道存在的意义 |
| 审计日志 | `IMcpAuditLog` 留宿主，实现走。日志落盘位置不要跟着模块跑 |
| 热重载 | 纯托管、无 WPF、无原生锁 → **真能热重载**。这是它比前端类模块值得搬的地方 |

### 度量提醒

**"宿主越小"不是目标，"能不重启就换掉的比例"才是。**

Aurora 就是反例：搬出宿主了，但 `AvalonDock.dll` 被运行中的进程锁着，改它仍然要停服。
MCP 没有这个问题，所以这 2,560 行搬出去是**真**增量。

---

## 六、还没定的

1. **模块编号**——`2026-0XX-HistoryPortunus`，按现有序列往下取。
   不并进 Diana：Diana 是"知识与工具"，Portunus 是"对外传输"，混在一起以后还得再拆一次。
2. **CLI 声明的形状**——`CommandDescriptor` 上加一个"上 CLI"的标记即可，缺省不上。
   不要在这一步引入 Cli/Mcp/Web 三值枚举，那是分离之后的另一轮。
3. **Portunus 坏掉时 CLI 怎么办**——回退到文件路径 + `--install-module`。
   这条要在 CLI 骨架落地时验一遍，不能只写在文档里。
3. 本文件放桌面还是进 Vulcan 的 `b-Office/current/`。现在放桌面是为了不动仓里的文档治理；
   决定落地之后，第二、三节的结论应该写进 Vulcan 的《有效决策》。


---

## 第二轮实况（2026-08-21，MCP 迁出）

### 切口比方案画的窄：9 个文件里有 3 个不该搬

| 文件 | 行 | 为什么留在宿主 |
|---|---|---|
| `CommandCatalogCommands.cs` | 413 | 它注册的是 `vulcan.command.list / show / domains / manual`——**宿主的指令自省面**，不是 MCP 传输。搬走会让它们改名成 `portunus.*`，而且 Portunus 一坏就查不了指令，恰恰是最需要查的时候。文件从 `Services/Mcp` 移到 `Services/Commands` |
| `CommandSchemaExporter.cs` | 216 | 宿主的 `--export-command-manual` 是发布管线入口，不能依赖某个模块装没装上 |
| `CommandManualGenerator.cs` | 121 | 同上 |

实际搬走 1,808 行。宿主 `Services` **6,559 → 3,858**。

### 耦合是怎么解开的

目录指令对网关的依赖只有一个字符串：`gateway()?.Policy`。而它本身就是 `mcp.policy`
设置键的归一化，宿主自己读得到。于是 `Func<McpGateway?>` 换成 `Func<string>`，
归一化收敛到 `Core.McpSettingKeys.ResolvePolicy`。

对治理库的依赖是四个只读方法，返回值全退化为基本类型（计数、修订号）。新增
`Core.IMcpPromptGovernanceView`，实现方经 `CommandBus.McpGovernance` 注入——沿用
`Confirmation` / `FrontendExecutor` 已确立的「宿主留挂钩、模块填实现」模式，
也因为总线是模块经 `IModuleContext` 唯一拿得到的宿主共享对象。

### 装机时炸了三个模块，全是真问题

测试全绿（宿主 92/92、Portunus 22/22）也挡不住这一类：**它们编译于旧的宿主公开面**。

| 模块 | 症状 | 真实原因 |
|---|---|---|
| Aurora | 界面起不来（Fatal） | 自建了一整套 MCP：`McpGateway` + `PromptGovernanceStore` + `McpAuditRecorder` |
| Mercury | 命令集页挂载失败 | `CommandCatalogDetail` 换了命名空间 |
| Janus | 36 条掉到 1 条 | `HistoryRecorder` 自建 `McpAuditRecorder`，还用着 4.2.0 就删掉的 `SqlText` |

修的时候发现**三处都是死码**，不是"忘了适配"：

- Aurora 的 `EnableMcp` 全仓只被赋值一次，值是 `false`；`McpAuditLog`、`McpRemoteConfirm`
  从没人设过。那个「第二个 MCP 网关」自界面变成模块（DEC-008）起就没被构造过。
  连带挖出 `FrontendCommandCatalog.CreateProxy`（进程外前端的代理机制）与
  `RemoteConfirmDialog`——它们一直没暴露，只因为 Aurora 从没重编译过。
- Janus 的 `IMcpAuditLog` 实现唯一装配点就是 Aurora 那个从未被赋值的 `McpAuditLog`。

**教训**：模块长期不重编译，会让宿主早已删除的公开面在模块里"看起来还活着"。
公开面注销与模块重编译之间的时间差，就是这类死码的藏身处。

### 两个被抓到的问题

**改名把安全排除绕过去了。** `McpExposurePolicy` 写的是 `StartsWith("vulcan.mcp.")`，
指令改名成 `portunus.mcp.*` 后当场失效——`start` / `stop` / `autostart` 一并变成远端
可见工具，等于把「关掉正在服务你的那条通道」交给远端。

这是同一个错误的**第三次**（前两次：`debug.logflood` → `vulcan.log.flood`）。
改为按**指令类**判定（`<域>.mcp.<动作>` 的中段）：域会随归属变动，指令类不会。
安全排除必须挂在不随搬家改变的那一段上。

**实现 `IDisposable` 的模块会平白多出一条 `dispose` 指令。** 4.3.0 让宿主在拆除阶段
回收模块实例之后，`IDisposable` 就从模块的私事变成了与宿主的约定，而反射投影不认识它。
远端调用 `<域>.dispose` 等于拆掉半个模块。已加入生命周期契约排除名单。

### 数字断言换成集合断言

`PromptGovernanceExternalizationTests` 原本断言可见工具数等于 16。改名绕过排除后它变成 18——
「多了两个」说明不了多的是哪两个。改成断言可见集合**恰好等于**那组无关的宿主夹具指令，
这样多出任何一条都会直接指名。
