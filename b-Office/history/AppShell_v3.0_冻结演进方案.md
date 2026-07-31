# AppShell · v3.0 冻结演进方案

> 文档性质：**冻结前的最后一批改动 + 冻结契约 + 从 OHS 独立**（三件事一次说清）
> 日期：2026-07-28 ｜ 目标版本：**3.0.0（冻结基线）**
> 起草输入：OHS 侧框架源码 `2.7.5`、当时最后发布的独立包 `0.7.2` ｜ 当前实施工作树：`AppShellVersion=3.0.0`
> 新址：`2026-023-AppShell`（由 2026-020-OneHistoryStudio 继承，OHS 托管的独立项目）
> 状态：**实施中**（发布状态以当前源码、测试和《AppShell 3.0 冻结体检与已知限制》为准）

---

## 0. 一句话定义

**v3.0 = 把 AppShell 从"OHS 的一个子目录"变成"有冻结契约的独立底座"**：先把冻结后再也治不了的暗病一次性修完（这是最后的机会），再按远比演进期严格的准入门禁锁死 3.0.0，然后把 AppShell 整体迁出 020、由 023 独立维护，020 降级为纯包消费方。

**不做**：不加新功能、不改画面风格、不动指令语法。
（原先"不删数据库"的前提已在 OHS 2.7.5 的质量长跑中失效 —— 库已被抽干，v3.0 改为**清除空壳**，见 §2.1。）

---

## 1. 为什么现在必须冻结

### 1.1 病因不是"三个项目各自有 bug"

2026-07-27 AppShell 发 0.7.2（破坏性修订：删除固定主内容、工作页契约与 `page.*`），
2026-07-28 OHS 正在做 2.7.x 质量修复长跑，WBall 正在做 v3.4 质量修复。
三个消费方**同时踩在一块正在移动的地板上**——上层排查出的每个缺陷，都分不清是自己的还是地板刚挪的。

### 1.2 最要命的耦合：OHS 是源码引用，不是包引用

| 消费方 | 引用方式 | 后果 |
|---|---|---|
| **OHS Studio** | `Studio.csproj` 直接 `ProjectReference` 四个 AppShell 工程；`OHS.sln` 挂着全部 5 个工程 | AppShell 源码一改，OHS **同一个解决方案里立刻上翻**,无版本缓冲 |
| **OHS Studio.Service** | `ProjectReference` → `AppShell.ServiceHost` | 同上 |
| **WBall (022)** | `PackageReference 0.7.2` | 有版本缓冲；0.7.2 迁移实测只花 2 行改动 |

同为消费方，WBall 的迁移是可控的（钉版本、按自己的节奏升），OHS 是不可控的（源码级联动）。
**这不是 AppShell 质量问题，是消费方式的问题。** 独立化要解决的正是这一条。

### 1.3 双版本线已经在打架

`AppShellVersion.props` 写着 **2.7.5**（对齐 OHS 2.7.x），实际发布的包却是 **0.7.2**。
一个产物两套编号，谁也说不清"0.7.2 对应哪份源码"。v3.0 统一到**单一版本线**，且不再与 OHS 对齐。

---

## 2. 两条硬约束（先说不做什么）

### 2.1 数据库**删**——OHS 2.7.5 已经把它抽干，剩下的是空壳

本节结论在 2026-07-28 **推翻过一次**：起草时依据的是"OHS 大量消费 IDataService，删了就炸"。
用户指出 OHS 2.7.5 的质量长跑已掀掉库依赖，复核后确认属实，故改为**清除**。

**复核证据（2026-07-28，020 工作树 `AppShellVersion=2.7.5`，HEAD `41ebc2c1`）**：

| 检查项 | 结果 |
|---|---|
| `SqliteDataService` 实现 | **已从框架删除**（`grep -rln "class SqliteDataService"` 无命中） |
| `Microsoft.Data.Sqlite` 引用 | 框架源码与工程文件**零引用**；只剩 `Directory.Packages.props:12` 一条孤儿版本声明 |
| `McpAuditRecorder` | 正路已是 `McpAuditRecorder(string dataDirectory, IShellLog)` → `state/mcp-history.jsonl`；`IDataService` 重载已标 `[Obsolete]` |
| `PromptGovernanceStore` | 同上，路径构造为正路，`IDataService` 重载已 `[Obsolete]` |
| OHS 装配点 | `ShellConfig` 不再设 `DataService`（原 `DataService = remoteData` 已移除），且 `EnableMcp = false` |
| OHS 业务侧 | `HistoryRecorder` / `ToolSyncService` / `PromptGovernanceStore` 均改吃 `paths.Root`；残留的 `IDataService` 构造是 `[Obsolete]` 转发桩 |
| OHS 表窗口 | 不再注册（`Id = "table"` 无命中，仅测试夹具用到同名字符串） |
| WBall (022) | v3.4 已取消数据库：传 `null` 编译通过、启动正常、四条确定性哈希逐字未变 |

**也就是说：两个消费方都已经不吃库了，框架里留下的是一具没人用的空壳。**
冻结一具空壳是最坏的选择 —— 它冻在契约面里，之后谁都不敢动，还会持续误导"后面有个数据库"。

**v3.0 的清除范围**（见 FZ-00）：

| 删 | 连带处置 |
|---|---|
| `AppShell.Core/Data/IDataService.cs` | 契约整体移除（连 `DefaultConnection` 常量） |
| `AppShell.Services/Web/RemoteDataService.cs` | 远程 db 门面随之移除 |
| `AppShell.Shell/Table/TableView.xaml(.cs)` | **表窗口取消**；`Id="table"` 不再被 Shell 接管 |
| `db.*` 内建命令组（`BuiltinCommands.RegisterDb` + `BuiltinCommandDefinitions` 里的 db 定义） | 命令面移除；`FrontendCommandCatalog.CatalogDataService` 一并清理 |
| `ShellConfig.DataService` 字段 | 装配面移除；`ShellWindow.xaml.cs:89/:380` 两处判空分支随之消失 |
| `McpAuditRecorder` / `PromptGovernanceStore` 的 `[Obsolete]` `IDataService` 重载 | 删除，只留路径构造 |
| `Directory.Packages.props` 的 `Microsoft.Data.Sqlite` 版本声明 | 删除孤儿声明 |

**保留**：`EnableMcp` 不再与任何数据服务相关（这条与 FZ-01 合流 —— 库删掉后，
"MCP 依赖 DataService"这个耦合自然消失，FZ-01 退化为"确认留痕/治理只走文件实现并删掉旧分支"）。

> **前提校验（M1 第一步必须做）**：清除前必须再跑一次全库扫描，确认除 020/022 外
> 没有第三个消费方还在用 `DataService`/`db.*`/表窗口（尤其 `b-Code-Samples`、`PackageSmoke`、SE2SW）。
> 有则先迁移该消费方，再删 —— 不允许"边删边修消费方"。

### 2.2 冻结代码的门禁必须远严于演进代码

演进期的暗病可以下一版修；**冻结期的暗病只能用一个 bug 去填另一个 bug，越填越烂**。
所以 v3.0 的准入门禁（§5）比平时严格得多，且**宁可推迟冻结，也不冻一个带病的版本**。
这也是本方案把"最后一批改动"（§3）放在冻结之前的唯一理由。

---

## 3. 冻结前的最后一批改动（最后的机会）

只收**冻结后再也没法治**的问题。每条都附本轮实测证据，不收"感觉可以更好"的。

### 3.0 FZ-00（P0）清除数据库空壳

**现象**：OHS 2.7.5 与 WBall v3.4 都已不吃库（§2.1 逐项证据），但框架里仍留着
`IDataService` 契约、`RemoteDataService`、`TableView`（表窗口）、`db.*` 命令组、
`ShellConfig.DataService` 字段，以及两处 `[Obsolete]` 的 `IDataService` 构造重载。

**改法**：按 §2.1 的清除范围表整体移除。

**为什么必须冻前做**：`IDataService` 与 `ShellConfig.DataService` 都在 public 契约面上。
冻结后想删就是破坏性修订（排 4.0），于是这具空壳会**在整个冻结期里挂着**——
既误导"后面还有个库"，又让每个新消费方都要判断"我要不要传 DataService"。
这是"冻结带病版本"的教科书案例，也正是 §2.2 要防的事。

**验收**：全框架 `grep IDataService` 零命中；四包不含 `Microsoft.Data.Sqlite`；
OHS 与 WBall 各自回归全绿；`command.list` 中不再出现 `db.*`。

### 3.1 FZ-01（P1）MCP 留痕与治理只走文件实现

**现象（起因）**：WBall 取消数据库后，`command.list` / `mcp.*` / `prompt.*` 整组框架命令消失 ——
`ShellConfig` 当时写明"EnableMcp 依赖 DataService，未配置则自动降级为关闭并告警"。

**现状（2.7.5 复核）**：`McpAuditRecorder` 与 `PromptGovernanceStore` 的**正路已经是路径构造**
（`state/mcp-history.jsonl`），库构造只剩 `[Obsolete]` 重载。所以本条已从"设计解耦"退化为**收尾**：

1. 随 FZ-00 删掉两个 `[Obsolete]` 重载；
2. 删除 `EnableMcp` 与数据服务相关的降级判断（`ShellWindow.xaml.cs:109` 那半个条件）与文档措辞；
3. **回归验证**：不配置任何数据服务时，`EnableMcp=true` 必须真正起得来，
   `command.list` / `mcp.*` / `prompt.*` 完整可用 —— 这是 WBall 当初丢命令的原始诉求，必须有测试兜住。

### 3.2 FZ-02（P0）MCP 默认端口按应用派生

**现象**：`McpGateway.cs:34` 硬编码 `DefaultPort = 8737`，`EnableMcp` 默认 `true`。
于是**每个派生应用启动都抢同一个端口**，先到先得。WBall 实测日志：

```
[Warn] [mcp] 自启动失败: 监听失败(端口被占用?): Failed to listen on prefix
'http://127.0.0.1:8737/' because it conflicts with an existing registration on the machine.
```

OHS 常驻占着 8737，**WBall 的 MCP 从 v3.0 包引用起就没起来过**。派生应用越多撞得越狠。

**改法**：默认端口由**应用标识哈希**派生（如 `8737 + hash(AppName) % 200`），并在占用时自动顺延重试 N 次；
`mcp.port` 显式设置仍然最高优先。落到日志里要能看出"实际监听端口"。

### 3.3 FZ-03（P0）新注册工具窗的比例塌缩

**现象**（WBall v3.4 实测）：0.7.2 下新增一个工具窗后 `win.list` 报 **`stage 停靠·上 3%`**；
在"全新布局"路径下更夸张——右侧全部窗口报 **1%**。WBall 只能靠**一次性删除旧布局文件**绕过去。

**根因**：`DockingHost` 的比例语义（`_ratios` + 星值固化像素）在"恢复旧布局 + 新窗口未在布局中"这条路径上拿不到稳定比例。
《二次开发演进手册》§3.3 已把这块列为踩坑区。

**为什么必须冻前做**：这是每个派生应用都会撞的路径，且解法在框架内部；
冻结后每个消费方都要各写一份"删布局"补丁——正是"用 bug 填 bug"的典型。

### 3.4 FZ-04（P1）中央区主内容成为显式契约

**现象**：0.7.2 删除 `ShellConfig.MainContent`，"中央区仅保留无标签空背景，不创建任何 LayoutDocument"。
派生应用的主画面（WBall 的对战舞台）只能**靠隐式约定**塞进中央列：`DockSide.Top/Bottom` 的窗格会被
`DockingHost` 插进中央列，所以看起来还在中间。**这是实现细节，不是契约**——它随时可能变。

**改法**（二选一，见 §10 Q2）：
- A：`DockSide.Center` 显式表达"占据中央区"，语义写进契约文档；
- B：不加枚举，但把"Top/Bottom 插入中央列"写成**契约文档 + 冻结测试**，从此不许改。

### 3.5 FZ-05（P0）统一命令目录与治理面，执行位置保持可分

**现象**：前端新增命令只进入桌面 `CommandRegistry` 时，服务端注册表不知道它存在，因此该命令不会稳定进入
`command.list` / `command.show`、MCP schema 和提示词治理。当前服务端虽然会通过
`FrontendCommandCatalog.CreateFrameworkProxies()` 投影 AppShell 内置前端命令，OHS 也会额外投影一部分
应用前端命令，但这仍依赖各消费方记得手工补代理；新命令漏接后可以正常编译和在 UI 使用，暗病只在服务端
查阅或 MCP 治理时暴露。

**决策**：统一的是**服务端权威命令目录与治理面**，不是把所有 handler 强制搬到后端。

```text
服务端唯一命令目录
├─ Backend：业务、文件、状态、模块、MCP 等命令在服务端执行
├─ Frontend：窗口、布局、焦点、选择、剪贴板和 UI 对话框在前端执行
└─ Hybrid：服务端完成业务，前端只负责确认、选择或展示
```

所有命令无论执行位置，都必须先进入服务端权威注册表，并具有同一份名称、参数、摘要、示例、只读性、危险性和
`ExecutionSite` 元数据。Help、命令集、`command.*`、MCP schema、提示词治理和自动命令手册只读取这份注册表。
前端只绑定或发布执行能力，不再维护第二份可漂移的命令定义。

**迁移规则**：

1. `proj.*`、`git.*`、`tool.*`、配置、文件和领域状态等业务命令默认迁到后端执行；
2. `win.*`、`layout.*`、焦点、选择、剪贴板、文件选择器和必须操作 WPF 对象的命令保留
   `ExecutionSite=Frontend`；
3. 桌面与 Service 共用的命令定义下沉到 Core 或应用共享程序集，两个宿主只绑定各自 handler；
4. 消费方新增前端命令时，构建管道必须自动生成服务端代理或由受校验的能力目录同步，禁止再手工维护名字清单；
5. 前端未连接时，命令仍可被查阅和治理，但运行时必须明确显示 `unavailable`，不得假装执行成功；
6. “可查阅、可治理”不等于“允许 MCP 执行”。纯 UI 命令默认不向 MCP 开放执行；需要开放时必须由描述符
   显式声明、当前存在可用前端，并继续经过只读/危险确认策略；
7. MCP 发起的前端命令不能依赖“请求来源本身就是 Shell 会话”的偶然关系，必须有明确的目标前端选择、
   会话绑定和断线失败语义。

**验收**：

- 在前端新增一条测试命令后，无需修改 Service 名字清单即可出现在服务端 `command.list/show` 和提示词治理；
- 同一命令在桌面、Service、MCP schema 和自动手册中的元数据逐字段一致；
- 前端离线时仍能查阅与提交提示词提案，执行返回“前端不可用”；
- 未显式允许 MCP 执行的 UI 命令不会出现在 MCP `tools/list`；
- 显式允许的测试 UI 命令只在目标前端在线时中继，断线、多个前端和确认超时均有确定结果。

**为什么必须冻前做**：命令所有权、代理生成、执行站点和会话中继都是 Core/ServiceHost 公共契约。若 3.0 冻结后
仍保留双注册表，后续每个消费者新增前端命令都要继续手工补服务端代理，提示词治理也会永久缺一块。

### 3.6 FZ-06（P1）冻结前体检：暗病扫一遍

冻结前必须对全框架跑一遍以下体检（每项要么修，要么写进"已知限制"）：

| 体检项 | 判据 |
|---|---|
| 无界集合 | 所有长期存活的 `List/Dictionary/Queue` 必须有容量上限或清理点（WBall 侧曾发现 `BattleDirector.Events` 无界；框架侧同类需自查） |
| 未释放资源 | `HttpListener`、定时器、文件监视器、COM 对象的 Dispose 路径完整，异常路径也走得到 |
| 硬编码 | 端口、路径、超时、容量上限一律可配，且默认值写进文档 |
| 跨应用共享资源 | HTTP.sys 前缀、命名互斥体、文件锁、`%AppData%` 目录：多实例并存不得互斥失败 |
| null 降级路径 | `DataService`/`Workspace`/`CommandSelection` 等可空装配项，为 null 时全链路可用（不是"能启动"而是"功能可用或明确禁用"） |
| 线程亲和 | UI 线程要求（`RequiresUiThread`）与实际调用一致；后台线程不碰 WPF 对象 |

### 3.7 BASE-273-276（P0）V3 主开发完成后的 OHS AppShell 基线追平

**为什么单独放在最后**：023 独立项目的 Git 基线是 OHS 2.7.3（`5198b921`），而 OHS 后续在 2.7.5、
2.7.6 又修过一批 AppShell 本体问题。若 V3 只完成 FZ-00~06 就冻结，未来 OHS 2.8 切到 AppShell 3.0 包时，
这些已经在 OHS 源码列车中治过的暗病可能重新出现。

本阶段必须在 **FZ-00~06 全部实现并稳定之后、API 快照和 3.0.0 正式发布之前**执行。原因是 V3 已经删除
数据库空壳并调整公共契约，不能直接把 OHS 提交整体 cherry-pick 回来；必须以完成后的 V3 源码为基准，
逐条判断“已由 V3 覆盖 / 仍需补入 / 因 V3 新设计而不再适用”，只移植仍有效的框架行为和回归测试。

**历史范围已经封口**：2.7.4 没有 AppShell 本体变化；需要对账的来源只有 2.7.5 `89172c1d`、
2.7.6 `e3df9648` 和隔离资产审计修复 `1a488517`。后续施工按下表执行，不要求再次通读 OHS 版本文档。

| 编号 | OHS 2.7.5~2.7.6 的 AppShell 变化 | V3 基线更新要求 |
|---|---|---|
| BASE-01 | `AppShellVersion.props` 成为程序集、包、README、PackageSmoke、manifest 的单一版本源 | 保留机制，版本值统一为 `3.0.0`；发布脚本不得另存版本常量 |
| BASE-02 | 发布脚本增加洁净检查、互斥锁、不可覆盖 feed、manifest、SHA-256、漏洞审计和资产恢复 | 逐项在 023 发布 staging 验证，任何一项不得因独立化而退化 |
| BASE-03 | `BuiltinCommandDefinitions`、`StandardWindowIds`、危险性投影、`CommandBus.Validate()`、`DomainsOf()` | 与 FZ-05 合并，以 V3 的服务端权威目录为最终形态；禁止恢复平行字符串和手工代理清单 |
| BASE-04 | `FrontendCommandCatalog` 从真实桌面注册事实投影前端能力，Help、命令目录、MCP schema 同源 | 纳入 FZ-05 验收；V3 新增或删除命令后四个投影必须一致 |
| BASE-05 | MCP 审计、提示词治理转为文件状态；`SqliteDataService` 退出依赖图 | 由 FZ-00/01 覆盖；确认四包不再携带 `Microsoft.Data.Sqlite`，不得恢复旧构造分支 |
| BASE-06 | `DockingHost` 的中央区合法性、自愈、比例、标签组、布局保存竞态和固定工具窗边界修复 | 与 FZ-03/04 合并；为每个仍适用的修复保留冻结回归，不能只核对最终画面 |
| BASE-07 | `ModuleCatalogSnapshot` 通过同一 `CommandBus` 读取 `module.list`/`command.list`，按 `Source`/`SourceDetail` 关联 | 保留来源过滤、跨代数量不一致拒绝、失败和选择失效时清空旧详情；不得按命令前缀猜模块 |
| BASE-08 | `ShellServiceClient` 为结构化反序列化错误补充命令名和 `JsonValueKind`，同时避免泄露 token/完整敏感路径 | 保留通用客户端错误语义，并用 OHS 结构化结果做消费验证 |
| BASE-09 | 标准 `tests/AppShell.Tests` xUnit 工程进入解决方案 | 把 BASE-07/08 和 FZ 回归放进标准测试工程，不依赖 OHS Smoke 才能发现框架回归 |
| BASE-10 | PackageSmoke 固定 `win-x64`、framework-dependent、10 MB 上限；构建/测试/审计使用同一隔离 `ArtifactsPath` | staging 必须在干净资产目录通过，并拒绝非目标 RID；不得依赖源码树预先存在的 `obj` |

**明确不导入**：OHS 2.7.6 的 `proj.tree` DTO、项目树业务字段和产品页面属于 OHS，不进入 AppShell。
V3 只保留 `ShellServiceClient` 对任意结构化结果的通用传输与诊断能力。否则看似“补基线”，实际会重新把
AppShell 绑回 OHS。

**执行与验收顺序**：

1. 为 BASE-01~10 建立逐项映射，记录 V3 对应源码、测试和“已覆盖/需补入/不适用”结论；不得只写
   “提交已合并”。
2. 对“需补入”项先补失败复现，再按 V3 结构实现；同一项若已被 FZ 改造替代，测试应验证新合同而不是
   恢复旧 API。
3. 运行 `AppShell.sln` Debug/Release、`AppShell.Tests`、隔离 PackageSmoke、漏洞审计和 3.0.0 staging。
4. 用 staging 的 3.0.0 包让 OHS 当前基线完成一次纯 `PackageReference` 消费验证，重点覆盖模块详情、
   服务端/前端命令目录、`proj.tree` 结构化结果传输、布局恢复和前端离线诊断。
5. 将 BASE-01~10 的最终结论写入 3.0.0 版本记录；存在未覆盖项时不得进入 M3，不得以“未来 OHS 2.8
   再修”作为放行理由。

---

## 4. 冻结的定义（冻的到底是什么）

**冻结 = 契约冻结 + 版本钉死，不是"仓库只读"。**

| 冻的 | 不冻的 |
|---|---|
| `AppShell.Core` 全部 public 签名（指令语法、`CommandRegistry/Bus/Descriptor`、`ToolWindowDescriptor/DockSide`、`IShellLog`、`PanelDefinition`、`IShellUiRegistrar`）<br>※ `IDataService` 已按 FZ-00 移除，不进冻结面 | 内部实现（private/internal）在不改行为的前提下可重构 |
| `ShellConfig` 的字段语义与默认值 | 新增**可选**字段（默认值不改变既有行为）——按 §6 走小版本 |
| 已发布包的版本号与产物哈希（3.0.0 不可覆盖） | 文档、示例、测试 |
| 面板 JSON 结构、布局文件格式、`%AppData%` 目录布局 | —— |

**冻结后允许的唯一改动类型**：致命缺陷修复（崩溃、数据损坏、安全）。且必须满足 §6 的修复纪律。

---

## 5. 冻结准入门禁（比演进期严得多）

3.0.0 必须**全部**满足才允许打包发布。任何一条不满足 → 推迟冻结，不降标准。

1. **三消费方在同一候选版本上全绿**：OHS Studio（含 Studio.Service）、WBall、SE2SW/示例宿主；
   各自提供自己的回归锚点（WBall 的确定性哈希、OHS 的 Smoke 套、示例宿主的 PackageSmoke）。
2. **零警告**：Debug/Release 双配置 `TreatWarningsAsErrors`，分析器全开。
3. **格式门禁**：`dotnet format --verify-no-changes` 在 clean checkout 通过。
4. **public API 快照**：生成 `PublicAPI.Shipped.txt`（或等价 API 差异报告）并入库；
   **冻结后任何 public 签名变化 = 破坏冻结**，CI 直接红。
5. **§3.6 体检六项**全部有结论（修复或写入"已知限制"清单）。
6. **命令目录一致性**：前端、Service、MCP schema 与自动手册逐字段一致；新增前端命令不需要手工补名字清单。
7. **数据库清除干净**：全框架 `IDataService` 零命中、四包不含 `Microsoft.Data.Sqlite`、
   `command.list` 无 `db.*`；且**不配置任何数据服务时 MCP 与提示词治理完整可用**（FZ-00/FZ-01）。
8. **包完整性**：四包（Core/Services/Shell/ServiceHost）同版本、SHA-256 入 manifest、
   隔离 `PackageSmoke` 在**空包缓存**下通过（证明不靠本机残留）。
9. **回滚点**：0.7.2 的不可变归档包、manifest 与固定源码提交
   `52694de25825e026977743673c3aebb7f54ba912` 保留可用，且文档写明校验与回退步骤；该历史版本发布时明确未创建 Git tag，不补造历史标签。
10. **文档与契约一致**（§7.3）：手册、需求文档、版本记录三份对齐，无"五类窗口/SQLite/表窗口"等
    已被 v3.0 删除的描述；手册含"冻结期开发纪律"章；消费方无可漂移的手册副本。
    —— **文档不对齐 = 不许冻结**：冻结后代码动不了，消费方只剩文档可依，错的文档比错的代码更难收拾。

---

## 6. 冻结后的缺陷处理纪律（防"用 bug 填 bug"）

冻结期改一行都要过这四关，缺一不可：

1. **先有失败复现**：提交一个能稳定复现该缺陷的测试（框架内或消费方侧），先红后绿；
2. **只修根因**：禁止在消费方侧加绕行补丁来"掩盖"框架缺陷；
   若只能绕行，必须在"已知限制"里登记，并挂 issue 指向根因；
3. **补丁版本**：修复走 `3.0.x`，**只改缺陷相关代码**，不夹带重构、不夹带新功能；
4. **三消费方回归**：任一消费方回归红 → 修复不发布。

**破坏性改动一律排到 4.0**，且必须先有迁移文档 + 至少一个消费方完成试点迁移后才发。

---

## 7. 从 OHS 独立：023 项目落位

### 7.1 现状与逐项处置（023 根目录全清单，一条不漏）

`2026-023-AppShell` 已建（分支 `2026-023-AppShell`，由 020 整树复制而来），因此**它现在包含整个 OHS**。
下表覆盖 023 根目录的**全部 15 项**，每项都有明确处置，不留"其它"：

| # | 项 | 体量 | 处置 | 说明 |
|---|---|---:|---|---|
| 1 | `b-Code-AppShell/` | 891K | **留** | AppShell 本体（src / tests / eng / artifacts / workspace） |
| 2 | `z-Package-AppShell/` | 4.3M | **留** | feed / manifest / checksums / changelog / staging |
| 3 | `b-Office/` | — | **留（内容按 §7.3 筛）** | 只留 AppShell 专属文档 |
| 4 | `b-Code-Samples/` | 18K | **留** | 示例是框架门面，跟框架走（§10 Q3） |
| 5 | `Logo.png` | — | **留** | 模板保留件 |
| 6 | `Unused/` | — | **留** | 模板的备用 b 级文件夹位 |
| 7 | **`stage/`** | **117M** | **删** | OHS 发布暂存区，与 AppShell 无关 |
| 8 | **`b-Code-Studio/`** | 931K | **删** | OHS 桌面本体 |
| 9 | **`b-Code-Studio.Service/`** | 16K | **删** | OHS 服务宿主本体 |
| 10 | **`b-Publish/`** | — | **删** | 内含 OHS 发布产物（含 `AppShell.*.dll/pdb/xml`）；AppShell 自己的发布位由发布链重建 |
| 11 | **`b-Code-OneHistory-V1/`** | **174M** | **删** | OHS 历史版本 |
| 12 | `OHS.sln` | — | **删** | 换成新建的 `AppShell.sln` 作根解决方案 |
| 13 | `Directory.Build.props` | — | **改写** | 去掉 OHS 专属属性，改为 AppShell 自己的构建策略 |
| 14 | `nuget.config` | — | **改写** | AppShell 是生产方，不再需要指向自己的本地 feed；只留 nuget.org |
| 15 | `README.md` | — | **改写** | 从"OHS 项目说明"改为"AppShell 独立项目说明"（含版本线、冻结状态、消费方指引） |

**删除铁律**：删除只动 **023**，020 一律不碰（Q7）；且**每一项删除前必须确认 020 侧存在完整副本**
（`stage/` 与 `b-Code-OneHistory-V1/` 合计约 291M，误删无本地兜底）。

**b-Office 内的删除**（属第 3 项的细化，用户已点名）：

| 项 | 处置 | 理由 |
|---|---|---|
| `b-Office/evidence/` | **删** | 全是 OHS 的部署/迁移证据（`deploy-ohs-2.7.3`、`部署物清理清单-V2.3.3` 等）；其中 `deploy-appshell-0.7.2-*` 一份**先移入 `z-Package-AppShell/changelog/` 归档**再删目录 |
| `b-Office/versions/` | **删** | `37-V2.7.5-代码管道化手册.md` 是 OHS 版本手册 |
| `b-Office/meta/` | **删** | OHS 的使用说明 / 命令手册 / 项目库与备份 / 模块开发手册 / MCP 接入与安全 —— 均以 OHS 为主语。**例外见下** |
| `b-Office/README.md` | **改写** | 从 OHS 文档索引改为 AppShell 文档索引 |
| `b-Office/` 中的 AppShell 文档 | **留 + 谨慎重写（§7.3）** | AppShell 专属，且是消费方的唯一入口文档 |

> ⚠️ `meta/` 删除前的**逐份甄别**：`MCP接入与安全.md` 与 `模块开发手册.md` 可能含**框架级**内容
> （MCP 网关、模块托管都在 AppShell 里）。删前逐份判断：属框架的段落**抽取合并进 `b-Office/`**，
> 属 OHS 用法的整份删。**不允许整目录一删了之** —— 这两份是 AppShell 能力的对外说明。

### 7.2 020 侧要删的 AppShell 相关项（逐条，删前必须先做 7.4）

| # | 项 | 处置 |
|---|---|---|
| 1 | `b-Code-AppShell/`（源码 src/tests/eng/artifacts/workspace） | **删**（已迁 023） |
| 2 | `z-Package-AppShell/`（feed/manifest/checksums/changelog/staging） | **删**（已迁 023） |
| 3 | `OHS.sln` 里 5 个 AppShell 工程节点（Core/Services/Shell/ServiceHost/App）与 "AppShell" 解决方案文件夹 | **删** |
| 4 | `b-Code-Studio/Studio.csproj` 的 4 条 `ProjectReference` | **改为 `PackageReference` 3.0.0** |
| 5 | `b-Code-Studio.Service/Studio.Service.csproj` 的 `ProjectReference`（ServiceHost） | **改为 `PackageReference` 3.0.0** |
| 6 | `nuget.config` 的本地 feed 路径 | 指向 `..\2026-023-AppShell\z-Package-AppShell\feed` |
| 7 | `b-Publish/` 下的 `AppShell.*.dll/pdb/xml` | 由发布链重新产出，不再从源码工程直出 |
| 8 | `b-Office` 内 AppShell 专属文档（版本记录、二次开发演进手册、需求规划 README_3、PACKAGE.md 等） | **迁 023**，020 侧删除 |
| 9 | OHS 的构建/发布脚本中调用 `Publish-AppShell.ps1` 的环节 | **删**（AppShell 自己发版） |
| 10 | `AGENTS.md` / `README.md` 中描述"OHS 内含 AppShell"的段落 | 改写为"消费 023 发布的包" |

### 7.3 `b-Office/` 中的 AppShell 文档谨慎重写 ★本次迁移风险最高的一步★

**为什么它比代码更要紧**：OHS 与 WBall **都不翻 AppShell 源码，而是照着这几份文档写代码**。
文档写错一句，两个消费方就照着错的写；而 v3.0 冻结之后，代码不能随便动，**文档就成了唯一还能修的东西** ——
它必须先是对的。

现有四份（`b-Office/`）：

| 文档 | 大小 | 定位 | v3.0 处置 |
|---|---:|---|---|
| **`二次开发演进手册.md`** | 15.2K | **消费方的第一入口**：冻结区/谨慎区/自由区三级权限、踩坑记录、工作流 | **重点重写（§7.3.1）** |
| `通用窗口框架模板_需求规划_README_3.md` | 40.1K | 需求真相（架构不变量、五类窗口、Q 决策记录） | 按 §10 Q9 改"五类→四类"，并追加 v3.0 决策记录 |
| `maintenance/AppShell版本记录.md` | 14.9K | 版本史与里程碑技术要点 | 追加 3.0.0 条目（含 FZ-00~06 与冻结声明）；0.7.x 标"停更，仅回滚" |
| `README.md` | 0.8K | 文档索引 | 改写为 023 独立项目的文档索引 |

#### 7.3.1 《二次开发演进手册》重写要求

**重写纪律（先说不许做什么）**：

1. **逐节改，不重起炉灶**。原有章节编号、踩坑记录（尤其 §3.3 DockingHost 三大机制、
   §3.5 `DOTNET_EnableWriteXorExecute=0`、§7 的 PowerShell 传参坑）**是血泪换来的，一句不许顺手删**；
2. **只改被 v3.0 事实推翻的部分**，每处改动要能指回本方案的 FZ 编号或 §2.1 证据表；
3. 改完必须与《需求文档》《版本记录》**三份对齐**，不允许出现"手册说四类窗口、需求文档说五类"。

**必须改的地方（已逐行定位，共 9 处）**：

| 手册位置 | 现状 | 改成 | 依据 |
|---|---|---|---|
| §1 一分钟概览 | "五类标准窗口(主窗口 / **表窗口** / 控制台 / 控制窗口群 / 资源窗口)" | **四类**，删表窗口 | FZ-00 |
| §2 分层结构 | `AppShell.Shell(…/控制台/**表窗口**/资源窗口/…)` | 删表窗口 | FZ-00 |
| §2 分层结构 | `AppShell.Services(实现层:日志/设置/**SQLite**/工作区/布局存储)` | 删 SQLite | FZ-00 |
| §3.2 Core 公共契约表 | `IDataService / QueryResult / ColumnInfo ｜ Data/ ｜ 表窗口、db.*` | **整行删除** | FZ-00 |
| §3.3 DockingHost 三大机制 | 比例语义(W-05)的描述 | 按 FZ-03 修复后的**实际行为**重写；并补"新窗口在恢复旧布局时的比例来源" | FZ-03 |
| §4 谨慎区 | "不要把默认库换成别的(Q4 定了 SQLite)" | **删该例**，换成仍然成立的例子 | FZ-00 |
| §5 自由区 | "新增**数据库 Provider**(实现 IDataService)" | **删该项** | FZ-00 |
| §8 验收清单 | 第 4/9 条"表窗口↔控制台双向同步" | **删除并重编号**，说明 v3.0 移除表窗口 | FZ-00 |
| §9 已知限制 | "表窗口列排序是页内排序…跨页排序需手输 `db.query order=`" | **删除** | FZ-00 |
| §11 关键文件地图 | `Data/IDataService.cs`、`SqliteDataService.cs`、`Table/TableView.*` 三行 | **删除**；补 `Mcp/`、`Modules/` 等现役条目 | FZ-00 |

**必须新增的一章（v3.0 的核心变化）**：

> **§12 冻结期开发纪律**（新增，位置在"上报机制"之后）

冻结改变了三级权限的语义，这一点必须在手册里说死，否则继任者会拿演进期的习惯去改冻结代码：

- **冻结前**：冻结区/谨慎区/自由区三级 —— 自由区可以放手改；
- **冻结后（3.0.0 起）**：**整个框架都是冻结区**。唯一合法改动是"致命缺陷修复"，
  且必须过 §6 四关（先有失败复现 → 只修根因 → 补丁版本不夹带 → 三消费方回归全绿）；
- **消费方遇到框架缺陷时怎么办**：写进"已知限制"并挂根因 issue，
  **禁止在消费方侧加绕行补丁掩盖框架缺陷**（WBall v3.4 曾被迫用"删布局文件"绕开 FZ-03，
  这正是反面教材 —— 冻结后这类绕行会永久留在消费方代码里）；
- **破坏性改动一律排 4.0**，且需先有迁移文档 + 一个消费方试点迁移。

#### 7.3.2 手册的副本漂移问题（必须一并解决）

实测：`2026-022-WBall/b-Office/二次开发演进手册.md` 是一份 **15041 字节的副本**，
而 023 权威版是 **15180 字节** —— **两份已经不一样了**。

消费方各存一份可漂移的拷贝，等于"照着过期文档写代码"。v3.0 起：

1. **023 的 `b-Office/` 是唯一权威**；
2. 消费方（020/022）**删除本地副本**，README 里改为指向 023 的路径 + 适用版本号；
3. 若确实需要本地留档，必须在文件头写明 **`来源：2026-023-AppShell @ 3.0.0，只读副本，不得就地修改`**。

#### 7.3.3 其它保留/新建

- 保留：`b-Code-AppShell/`、`z-Package-AppShell/`、`b-Code-Samples/`；
- 新建根 `AppShell.sln`（替代 `OHS.sln`）、`README.md`（AppShell 自述）、`Directory.Build.props`；
- `b-Publish/` 删除后按需重建为 AppShell 自己的发布产物位（演示宿主 ZIP 等）。

### 7.4 顺序铁律：先换引用，再删源码

**020 必须先完成"ProjectReference → PackageReference 3.0.0"并构建通过，才允许删 `b-Code-AppShell`。**
顺序颠倒 = OHS 立刻无法构建，且没有回滚缓冲。

### 7.5 WBall（022）侧的连带

`nuget.config` 的本地 feed 相对路径需从
`..\2026-020-OneHistoryStudio\z-Package-AppShell\feed` 改为 `..\2026-023-AppShell\z-Package-AppShell\feed`；
包版本 `0.7.2` → `3.0.0`。WBall 是包消费方，改动量为 2 行 + 一次回归。

---

## 8. 版本号决策

- **废除双轨**：源码 `AppShellVersion=2.7.5` 与包 `0.7.2` 统一到**单一版本线**；
- **不再与 OHS 对齐**（用户 2026-07-28 明确取消该要求）；
- 冻结基线定为 **3.0.0**：它从 2.7.3 独立项目基线出发，完成 V3 改造后再按 §3.7 追平 OHS 2.7.6
  仍有效的 AppShell 修复；"3.0"同时宣告"独立 + 冻结契约"；
- 0.7.x 包线**停更**，保留可回滚；文档需给出 `0.7.2 → 3.0.0` 的版本对照表。

> 备选：把冻结基线定为 `1.0.0`（"首个稳定契约"更符合语义化版本的习惯用法）。
> 若选 1.0.0，需接受源码版本从 2.7.5 **回退**编号——本方案不推荐，见 §10 Q1。

---

## 9. 里程碑

```
M0  本方案评审拍板(§10 问题)
 → M0.5 全库扫描:确认除 020/022 外无第三方消费 DataService/db.*/表窗口(§2.1 前提校验)
 → M1  冻结前改动:FZ-00 清除数据库空壳 / FZ-01 留痕治理收尾 / FZ-02 端口派生 /
        FZ-03 比例塌缩 / FZ-04 中央区契约 / FZ-05 统一服务端命令目录与治理面
        (每条独立提交,各带失败复现测试;FZ-00 先做 —— 它删掉的面越早清,后面几条越好验)
 → M2  FZ-06 体检六项,逐项修复或登记"已知限制"
 → M2.5 BASE-273-276 基线追平:V3 完成态逐项核对 BASE-01~10,只补仍有效的框架行为与测试;
        用 3.0.0 staging 包完成一次 OHS PackageReference 消费验证,未覆盖项不得后移到 2.8
 → M3  §5 准入门禁全绿(三消费方同候选版本回归 + API 快照入库)
 → M4  发布 3.0.0(四包同版本 + manifest + 隔离 PackageSmoke),打 tag,冻结生效
 → M5  020 换引用(ProjectReference → PackageReference 3.0.0)并构建通过 ★不可跳★
 → M6  020 按 §7.2 逐条删除 AppShell 相关项;022 按 §7.5 改 feed 与版本
 → M7  023 清理 OHS 残留(§7.1 全清单逐项;删前核对 020 有完整副本)、
        新建 AppShell.sln/README/Directory.Build.props
 → M7.5 ★文档谨慎重写(§7.3)★:二次开发演进手册 9 处定位改动 + 新增"冻结期开发纪律"章;
        需求文档五类→四类;版本记录追加 3.0.0;消除 022 的手册副本漂移
 → M8  三消费方各跑一次完整回归,归档证据;OHS 注册 023 为独立项目
```

**M4 之前允许改 AppShell 代码，M4 之后进入 §6 纪律。**

---

## 10. 待拍板问题

| # | 问题 | 建议默认 |
|---|---|---|
| Q1 | 冻结基线版本号：3.0.0 还是 1.0.0？ | ✅ **3.0.0**（承接源码 2.7.5，不回退编号；"3.0"同时宣告独立与冻结） |
| Q2 | FZ-04 中央区：新增 `DockSide.Center`，还是把现有隐式行为写成契约+测试？ | ✅ **加 `DockSide.Center`**：隐式约定迟早被"顺手优化"掉，显式枚举才冻得住 |
| Q3 | `b-Code-Samples`（示例）归 020 还是 023？ | ✅ 归 **023**：示例是框架的门面，跟着框架走；OHS 侧不再需要 |
| Q4 | 023 里 OHS 专属文档（`b-Office/evidence`、`b-Office/meta`）怎么办？ | ✅ **删**（020 侧是权威副本，023 只留 AppShell 专属） |
| Q5 | 冻结期限多长？ | ⭕ 建议**按事件而非按时间**：三消费方各自完成当前质量长跑并稳定运行两周后，才评估是否开 4.0 |答复：按事件，而且我想这可能是长期冻结，因为除非第三个消费方存在否则基本上不会再动了
| Q6 | SE2SW 是否算正式消费方（要不要进 §5 门禁）？ | ⭕ 待你确认它的现状；若仍在用 AppShell，必须进门禁 |答复：不是
| Q7 | `b-Code-OneHistory-V1`（174M）与 `stage`（117M）在 023 里直接删，还是先确认 020 侧有完整副本？ | ✅ **先核对 020 副本完整，再删**；删除只动 023，020 不碰 |
| **Q8** | FZ-00 的删除范围：`IDataService` 连同**表窗口 + `db.*` 命令组**一起删，还是保留表窗口（改成读别的数据源）？ | ✅ **一起删**。表窗口的内容供给完全建立在 `IDataService` 上，两个消费方都已不注册它；留一个空表窗口等于把废契约冻进 3.0 |
| **Q9** | 五类标准窗口里少掉"表窗口"，要不要同步改《需求文档》的架构不变量描述？ | ✅ 要。`b-Office/通用窗口框架模板_需求规划_README_3.md` 的"五类标准窗口"需改为四类并注明 v3.0 移除原因，否则文档与契约不一致 |
| **Q10** | 框架侧不再有任何 `IDataService` 实现后，消费方若确实需要表格 UI 怎么办？ | ⭕ 自备：消费方在自己的工具窗里实现（WBall 已是此模式）。框架不再提供通用表格，也不再假装提供 |答复：其实GPT给了一个好方法，就是表格工具和数据库到时候如果真需要可以写一个模块专门承载

---

## 11. 风险与回滚

| 风险 | 控制 |
|---|---|
| M5 换引用后 OHS 编译不过（源码引用与包引用的可见性差异，如 `internal`） | M5 单独一批、单独提交；不过则回退该提交，AppShell 侧补 `InternalsVisibleTo` 或提升可见性后重发 3.0.1 |
| 删 `b-Code-AppShell` 后发现 OHS 还有隐藏依赖 | 删除前先在 020 跑一次"移走目录"的模拟构建（改名为 `.bak` 试构建），通过才真删 |
| 冻结后出现必须改契约的缺陷 | 走 §6：能绕则登记"已知限制"，不能绕则排 4.0 并附迁移文档；**不允许悄悄改 3.0.x 的 public 面** |
| 023 清理误删 OHS 唯一副本 | Q7：删前逐项核对 020 侧存在且完整；只删 023，020 不动 |
| 双版本线切换造成消费方还原到旧包 | Z 级 feed 只保存当前正式版本；0.7.2 在 `b-Publish/feed`、manifest 与 checksums 中不可变保留并标注“停更，仅回滚用”，回滚时显式临时切源 |
| V3 从 2.7.3 分叉导致漏掉 2.7.5/2.7.6 已修暗病 | M2.5 按 BASE-01~10 对账并用 staging 包做 OHS 消费验证；未覆盖项阻断 M3 |

---

**批准本方案后回复"开冲 AppShell v3.0"即执行 M1~M8；对任一项有异议请直接改本文件或口头拍板。**

—— 文档结束 ——
