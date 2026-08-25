# HistoryVulcan 模块 API

适用宿主版本：**5.1.2**。

本文件是模块作者唯一需要的宿主合同。HistoryVulcan 只提供模块注册器、命令总线和模块开发管线；
界面与传输能力由认领模块维护，不属于宿主模块 SDK。

## 1. 引用宿主快照

模块默认引用已发布的 `z-Publish/host/HistoryVulcan.*.dll`，并设为 `<Private>false</Private>`。不要自动回退到
HistoryVulcan 源码工程；需要联调时显式提供项目自己的开关，并在构建前验证引用目标存在。

模块候选包采用版本化目录：`z-Publish/HistoryX-vX.Y.Z/`。每个包必须包含：

- `module.manifest.json`
- 模块程序集和 XML 文档
- `SHA256SUMS`
- `docs/` 下的模块消费文档

模块版本必须同时对齐版本 props 或项目文件、源 `module.manifest.json` 和根 `project.manifest.json`。

## 2. 模块入口

模块只依赖以下公开面：

| 类型 | 使用方式 |
| --- | --- |
| `IModuleContext` | 通过 `Bus` 执行命令，通过 `RegisterCommands` 注册模块命令。 |
| `IModuleContextAware` | 在 `Attach` 保存模块上下文。 |
| `ModuleInfoBase` | 提供模块名、版本和主类型身份。 |
| `CommandRegistry` / `CommandDescriptor` | 声明命令名称、参数、摘要、处理器和元数据。 |
| `CommandBus` / `CommandResult` | 统一执行命令并返回文本与可选结构化数据。 |

`IModuleContext` 不提供设置、日志、数据目录或 UI 生命周期。模块需要持久状态时由自身管理；需要宿主命令时走总线。
不要实现已经移除的 `IUiModule`、`IShellUi*`，也不要复制 `ModuleHost`。

## 3. 命令契约

模块命令恒为三段式小写名称：`<模块域>.<类>.<方法>`。模块域去掉 `History` 前缀，例如
`HistoryPortunus` 使用 `portunus.*`，`HistoryJanus` 使用 `janus.*`。

每条命令至少声明 `Name`、`CommandClass`、`Summary` 和 `Handler`；有输入时声明 `Parameters` 与 `Example`。
`Readonly` 表达是否写入，`Level` 表达是否需要交互确认，`HiddenReason` 表达不应出现在通用命令目录的原因。
命令处理器不直接读写宿主窗口、模块槽或宿主源码目录。

模块可用 `CommandDescriptor.Annotations` 表达模块自有元数据；宿主不解释该数据。需要活对象时放在
`CommandResult.Data`，调用方负责理解其类型与生命周期。

## 4. 模块运行包

运行包只安装到 `%AppData%\HistoryVulcan\Modules\<模块名>`。安装会先校验 manifest 和完整 SHA256 清单，
在运行区外暂存，卸载同名模块，原子替换，再建立新模块快照；失败时恢复原包。

模块作者不手工拷贝 AppData，也不改变发现根。正常开发使用 `vulcan.dev.submit` 和 `vulcan.dev.finish`：二者在
候选构建、测试与提交成功后自动安装并热重载。运行时状态以 `vulcan.module.list` 为准。

## 5. CLI 合同

同目录 `HistoryVulcan.Cli.exe` 是模块开发入口：

```text
HistoryVulcan.Cli.exe --cli vulcan.dev.start project=<项目> slug=<问题> agent=<AI>
HistoryVulcan.Cli.exe --cli vulcan.dev.submit name=<模块> msg=<说明> worktree=<工作区> [allowDirty=true]
HistoryVulcan.Cli.exe --cli vulcan.dev.finish name=<模块> msg=<说明> worktree=<工作区>
```

`--cli` 固定运行离线组合，不会连接或启动第二个宿主。开发三步只给模块，宿主自身使用
`vulcan.release.cycle`。`submit` 脏树默认拒绝，版本或源码改动已经确认时显式传 `allowDirty=true`；
`dryRun=true` 只报告计划，不构建、不写候选、不提交、不安装。

脚本使用 `--format json` 时 stdout 只输出一个结果对象，字段固定为 `runId`、`success`、`exitCode`、
`executionTarget`、`candidatePath`、`installedPath`、`runtimeAck`、`logPath`、`diagnostics`。

`--runtime` 只用于查询已运行宿主的受限状态或执行批准过的 reload；管线安装由 `submit/finish` 负责。
