# HistoryVulcan

HistoryVulcan 是独立维护的通用桌面应用框架，也是框架源码的唯一真值。

## 结构

- `src/HistoryVulcan.Core`：指令、停靠、日志、MCP 和存储契约。
- `src/HistoryVulcan.Services`：日志、设置、文件状态、MCP 与模块托管实现。
- `src/HistoryVulcan.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `src/HistoryVulcan.Shell`：WPF 主壳、停靠窗口和内置命令。
- `src/App`：框架演示宿主，用于独立构建和 GUI 验收。
- `eng`：发布、公开 API 冻结、TRX 失败摘要和 AI-ready 项目合同检查。
- `../b-Office/package`：消费文档编辑源；`../b-Office` 根目录保留冻结合同和内部设计记录。
- `../b-Publish/current`：唯一一份当前候选和完整发布测试结果。
- `../b-Publish/history`：按版本保存的 Z 级最小正式历史副本。
- `../z-HistoryVulcan/host`：当前正式 HistoryVulcan 宿主程序；Z 根目录同时保留精简复用说明和兼容消费资产。

## 构建

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Debug --no-restore
dotnet build .\HistoryVulcan.sln -c Release --no-restore
```

正式 HistoryVulcan 从 Z 快照运行；需要兼容嵌入式框架消费时使用单独批准的固定版本包，不直接引用本目录源码。

## 3.x 宿主

3.0.3 是冻结基线；当前源码目标为 3.3.0（DEC-022：命令硬切 `vulcan.<类>.<方法>`，快捷键/命令工作台由 HistoryMercury 4.1.0 拥有）。
3.1.8 是不受支持的内部过渡版本，
不得作为新消费基线。当前正式交付物是 win-x64、依赖 .NET 8 Desktop
Runtime 的 HistoryVulcan 宿主，不生成 NuGet 包。兼容包合同继续保留，但必须从单独批准的同版本包源消费。

宿主候选覆盖写入仓库内 `b-Publish/current`，同时包含运行程序和消费文档；审核通过后一次性更新
整个 `z-HistoryVulcan`。宿主候选与部署入口：

```powershell
# 可覆盖 current：生成 Release 宿主、UI/消费文档和 SHA-256 清单
.\eng\Build-HistoryVulcanPackage.ps1

# 候选审核通过后部署同一完整快照到 Z
powershell -NoProfile -ExecutionPolicy Bypass -File ..\2026-019-HistoryDiana\b-Code\Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish
```

当前交付物是宿主程序，不生成 NuGet 包。旧 `Publish-AppShell.ps1` 已于 3.3.2 退役（DEC-023），
`Build-HistoryVulcanPackage.ps1` 只生成候选；正式提升入口是 Diana 集中发布脚本。
脚本不会执行 Git commit/tag/push，也不会推送 NuGet.org。包结构与许可边界见 `PACKAGE.md`。
完整消费文档由 `../b-Office/package` 生成，候选位于 `../b-Publish/current/docs/`，正式历史位于
`../b-Publish/history/<版本>/docs/`，并随当前正式快照写入 `../z-HistoryVulcan/docs/`；
其他项目和 AI 先读
`../z-HistoryVulcan/HistoryVulcan.reuse.md`，再按需跟随其中的合同链接。

## 维护门禁

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Assert-PublicApiBaseline.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\eng\Test-ProjectContract.ps1 -Instantiation
```

现行项目规则从 `../project.manifest.json` 和 `../b-Office/current/` 读取；历史施工与冻结证据默认不进入维护上下文。
