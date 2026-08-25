# HistoryVulcan 5.1.1

HistoryVulcan 是独立维护的通用桌面应用框架，也是框架源码的唯一真值。

## 结构

- `HistoryVulcan.Core`：指令、停靠、日志、MCP 和存储契约。
- `HistoryVulcan.Services`：日志、设置、文件状态、MCP 与模块托管实现。
- `HistoryVulcan.ServiceHost`：无窗 WPF 服务宿主、确认通道和 `svc.*` 生命周期。
- `App`：HistoryVulcan WinExe 宿主入口。桌面 Shell 已迁往 HistoryAurora。
- `Cli`：同目录 `HistoryVulcan.Cli.exe` Console 入口；`--cli` 是离线组合，`--runtime` 是不可降级的命名管道通道。
- `../b-Code-Eng`：公开 API 基线、发布登记表、可选安装包脚本和 CI 失败摘要。
- `../b-Office/package`：消费文档编辑源；`../b-Office` 根目录保留冻结合同和内部设计记录。
- `../z-Publish`：唯一一份当前候选和完整发布测试结果。
- `../z-Publish/history`：按版本保存的 Z 级最小正式历史副本。
- `../z-Publish/host`：当前正式 HistoryVulcan 宿主程序；Z 根目录同时保留精简复用说明和兼容消费资产。

## 构建

```powershell
dotnet restore ..\HistoryVulcan.sln --locked-mode
dotnet build ..\HistoryVulcan.sln -c Debug --no-restore
dotnet build ..\HistoryVulcan.sln -c Release --no-restore
```

正式 HistoryVulcan 从 Z 快照运行；需要兼容嵌入式框架消费时使用单独批准的固定版本包，不直接引用本目录源码。

## 5.1.1 宿主

3.0.3 是冻结基线；当前源码目标为 5.1.1（CLI 合同、运行时 IPC 和十类命令分类已纳入现行基线；快捷键/命令工作台由 HistoryMercury 拥有）。
3.1.8 是不受支持的内部过渡版本，
不得作为新消费基线。当前正式交付物是 win-x64、依赖 .NET 8 Desktop
Runtime 的 HistoryVulcan 宿主，不生成 NuGet 包。兼容包合同继续保留，但必须从单独批准的同版本包源消费。

宿主候选覆盖写入仓库内 `z-Publish`，同时包含运行程序和消费文档；审核通过后一次性更新
整个 `z-Publish`。入口是宿主进程内管线：

```text
HistoryVulcan.Cli.exe --cli vulcan.release.cycle name=HistoryVulcan msg=candidate worktree=<工作区>
HistoryVulcan.Cli.exe --cli vulcan.release.cycle name=HistoryVulcan msg=publish
```

当前交付物是宿主程序，不生成 NuGet 包。旧 `Publish-AppShell.ps1` 已于 3.3.2 退役（DEC-023）。
管线不会执行 Git tag/push，也不会推送 NuGet.org。包结构与许可边界见 `PACKAGE.md`。
完整消费文档由 `../b-Office/package` 生成，候选位于 `../z-Publish/docs/`，正式历史位于
`../z-Publish/history/<版本>/docs/`，并随当前正式快照写入 `../z-Publish/docs/`；
其他项目和 AI 先读
`../z-Publish/manifest.json`，再按 `documents` 读取同一候选的消费合同。

## 维护门禁

```powershell
dotnet test ..\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj --filter FullyQualifiedName~ReleasePipelineTests
```

现行项目规则从 `../project.manifest.json` 和 `../b-Office/current/` 读取；历史施工与冻结证据默认不进入维护上下文。
