# 并行任务 CODEX-A5：提示治理从宿主外移

> 本文是给另一个 AI（Codex）的独立任务书。与 Claude 正在进行的 A2/A3/A4 **无文件重叠**，
> 可并行开发。开工前先读 `AGENTS.md`、`project.manifest.json`、`b-Office/current/技术合同.md`。

## 为什么做这件事

宿主当前对外投影 24 个 `vulcan_*` MCP 工具，其中 **8 个是提示治理**
（`vulcan.prompt.correct` / `corrections` / `diff` / `get` / `history` / `incidents` /
`propose` / `record`）——**占宿主整个 MCP 暴露面的三分之一**。

提示治理是"关于 MCP 工具描述的元管理"：谁改了工具描述、改成什么、谁批准、出过什么事故。
它不是宿主的运行时职责。宿主的职责是指令总线、模块装载、网关。把这 8 条留在宿主里，
每次评审宿主暴露面都要连带评审一套与运行无关的治理流程。

体量：`Services/Mcp/PromptGovernanceStore.cs` 396 行 + 提示治理命令注册（约 200 行）
+ 8 条命令的 schema 投影。这是 4.0 里**唯一还能真正减体积**的一块。

## 目标

把提示治理整体移出宿主，宿主只保留"读取当前生效描述"这一条运行时必需的能力。

### 必须保留在宿主的部分

MCP `tools/list` 要用生效描述来投影工具。因此宿主需要一个**只读**的描述查询接口：

- 保留：按工具名读取当前生效描述（默认描述 + 已应用修订的结果）。
- 保留：描述数据的存储位置不变（`%AppData%\HistoryVulcan\` 下现有表），
  **不得迁移或改写现有治理数据**——那是历史审计记录。

### 必须移出的部分

8 条 `vulcan.prompt.*` 命令及其背后的提案/批准/勘误/事故录入流程。

## 关键约束（违反任一条都算失败）

1. **不得丢历史数据。** `PromptGovernanceStore` 现有表（`mcp_descriptions`、
   `mcp_prompt_proposals` 等）已有真实修订与事故记录。迁移方案必须让现有数据在
   新归属下仍可读，或明确保留原表由宿主只读访问。哪种都行，但要在决策记录里写明并有测试。
2. **`tools/list` 的描述不能退化。** 移出后工具描述必须与移出前逐字一致。
   建议做法：移出前先 dump 一份全量 `工具名 → 生效描述` 快照，移出后逐条比对。
3. **公开面走冻结门禁。** 删除的公开成员进 `PublicAPI.Unshipped.txt` 的 `*REMOVED*`，
   并同步 `eng/public-api-baselines/4.0.0/`。**注意：4.0.0 基线目录 Claude 也在改**，
   见下面的冲突规避。
4. **禁止用抑制手段过门禁。** `Test-QualityGate.ps1` 会扫 `NoWarn` / `SuppressMessage` /
   `#pragma warning disable`，出现即失败。`Services` 工程开了
   `GenerateDocumentationFile` + 警告即错误，所有 public 成员必须有 XML 注释。
5. **UTF-8 BOM 规则。** `project.manifest.json` 必须无 BOM（`Test-ProjectContract.ps1`
   会卡）；`.cs` / `PublicAPI*.txt` / `*.props` 保持原有 BOM 状态。改文件前后各查一次，
   不要用会无条件加 BOM 的写法（如 Python `encoding='utf-8-sig'` 写一个本来无 BOM 的文件）。

## 文件边界（避免与 Claude 冲突）

**你可以改：**

- `b-Code-HistoryVulcan/src/HistoryVulcan.Services/Mcp/PromptGovernanceStore.cs`
- 提示治理命令注册相关文件（`Services/Mcp/` 下与 `vulcan.prompt.*` 注册有关的部分）
- `b-Code-HistoryVulcan/src/HistoryVulcan.Services/Mcp/McpAuditRecorder.cs`
- `b-Code-Tests/HistoryVulcan.Tests/` 下新增你自己的测试文件
- `b-Office/current/有效决策.md` — **只在文件顶部插入你的 DEC 条目**，不要改动已有条目
- `b-Office/current/技术合同.md` — **只新增你的 REQ 小节**，不要改动已有小节

**不要改（Claude 正在动）：**

- `src/App/` 全部（A3 正在拆分前后端组合根）
- `src/HistoryVulcan.ServiceHost/` 全部（A2/A4 正在改进程模型与确认通道）
- `src/HistoryVulcan.Services/Web/` 全部
- `src/HistoryVulcan.Services/Mcp/McpGateway.cs` 与 `McpGateway.Protocol.cs`
  —— 你需要改 `tools/list` 的描述来源时，**不要直接改这两个文件**，
  改为在你自己的新文件里提供接口，然后在交付清单里列出"需要 Claude 接入的一行改动"，
  由 Claude 合并时接上。
- `src/HistoryVulcan.Services/Mcp/CommandCatalogCommands.cs`（刚从 Shell 移入，Claude 在收尾）
- `VulcanVersion.props`、`project.manifest.json`、`README.md`、`AGENTS.md`

**公开面基线的特殊约定：** `PublicAPI.Unshipped.txt` 和
`eng/public-api-baselines/4.0.0/` 两边都要改，冲突风险高。你只**追加**行，
不要重排或删除他人添加的行。合并时以"两边追加的并集"为准。

## 验证（必须全绿才算完成）

按 `project.manifest.json` 的 `commands` 逐条跑：restore（`--locked-mode`）、
build、test、verify、publicApiGate、qualityGate，外加
`dotnet format .\HistoryVulcan.sln --verify-no-changes --no-restore`。

补充验收：

- 移出前后 `工具名 → 生效描述` 全量快照逐条一致（把比对脚本或测试留在仓库里）。
- `vulcan.command.list domain=vulcan mcp=visible` 的可见工具数从 24 降到 16，
  且减少的恰好是 8 条 `prompt.*`。
- 现有治理数据仍可读：写一条测试，用仓库里已有的真实表结构验证读取路径没断。

## 交付

- 一个 DEC 条目（写清数据归属方案与为什么这样选）
- 一个 REQ 小节（含可执行的验收方式）
- `b-Office/package/HistoryVulcan_消费变更摘要.md` 的破坏性变更条目**先不要写**，
  留一段草稿放在你的 DEC 里，由 Claude 合并时统一并入（该文件 Claude 也在改）。
- 末尾列出"需要 Claude 接入的改动"清单，逐条给出文件、位置、要接的那几行。

## 已知会踩的坑（Claude 这两天实测）

- `dotnet test --no-build` 在 build 失败时会**跑旧 DLL 并报"通过"**。永远先看 build 的
  exit code，不要把这种情况当通过。
- 运行中的宿主会锁 `bin\Release\net8.0-windows\HistoryVulcan.exe`，导致 Release 构建
  报文件占用。构建前先 `Stop-Process` 掉 `HistoryVulcan` 进程。
- `App` 类位于命名空间 `HistoryVulcan.App`，与类名同名。测试里引用要用别名：
  `using VulcanApp = HistoryVulcan.App.App;`
- 判断前端是否连上后台，看 `GET /api/health` 的 `shells`，**不要看命令条数**——
  网关会从 `web.frontendcatalog` 缓存恢复前端命令，前端没连也能看到它们。
- 访问 `/api/*` 需要 IPC 令牌：从
  `%AppData%\HistoryVulcan\service\endpoint.json` 的 `accessToken` 取，
  以 `Authorization: Bearer <token>` 发送，另需 `X-HistoryVulcan-Client: Shell` 头。
