# HistoryVulcan 模块 API

适用宿主版本：**5.1.2（冻结基线 `v5.1.2`）**。

本文件是模块作者唯一需要的宿主合同。HistoryVulcan 只提供模块注册器、命令总线和模块开发管线；
界面与传输能力由认领模块维护，不属于宿主模块 SDK。

5.1.2 起本合同是冻结的模块接入面。宿主可以重构内部实现，但不会在 5.x 内删除或改签下列
公开类型；新增通用能力必须先推进宿主版本并通过公开 API 门禁。模块不得据此依赖未列入本文的
Services 实现类型或宿主内部命令。

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

## 2.1 装载次序与就绪（5.1.3）

宿主一轮装载分两段：先把全部模块的程序集读进来收指令，再**按次序逐个 `Attach`**，
每接上一个立刻登记它的指令。因此：

- `Attach` 里能看到的，只有**排在你前面**已接入模块的指令；排在你后面的还没有。
- 想让某个模块排在你前面，在自己的 `module.manifest.json` 写 `dependsOn`：

  ```json
  { "name": "HistoryMercury", "dependsOn": ["HistoryAurora"] }
  ```

  只影响次序，不是硬前置：依赖不在运行区时你照样装载，宿主只记一条发现诊断。
  依赖成环或指向自己同样只记诊断，环内按名称序接入。不写 `dependsOn` 时次序是名称序。

- **在 `Attach` 里跨模块调用是错的做法**，即便次序排对了也脆弱：`Attach` 是你开始干活的
  地方，不是全宿主准备好的时刻。要在「全部模块都接上」之后做事，登记一条
  `<你的域>.host.ready`，宿主会在整轮装载完成后调用它一次（安静通道，不进指令历史）。
  没登记就跳过，宿主不因此改变任何行为。单包热装（`vulcan.module.install`）不触发它。

- 任何时候都可以问 `vulcan.module.ready`：装载中回「目录尚不完整」，
  完成后回已接上的模块数与未接上的名单。别用「等一会儿再拉一次」代替它。

界面模块尤其要注意：`Attach` 里开的线程会立刻去拉命令目录和各模块页面描述。
那个时刻目录必然是不完整的——把这类工作挪进 `host.ready`。

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
在运行区外暂存，卸载同名模块，原子替换，再只把这一包装回当前快照；失败时恢复原包。
清单覆盖模块包的不可变载荷；`history/` 是归档目录，`data/` 是模块运行态目录，两者不计入 SHA256SUMS。
manifest 的可选字段 `dependsOn`（字符串数组，模块名）声明接入次序，见 2.1。
模块的 `artifact`、`docs` 和 `deps` 不得放在 `data/` 下，升级时宿主保留已有 `data/` 内容。
不要整仓 `vulcan.module.reload`：那会拆除全部模块，界面模块可能变成 0 条指令。

模块作者不手工拷贝 AppData，也不改变发现根。模块管理页的“卸载模块”按钮调用
`vulcan.module.uninstall name=`，宿主会先卸载内存实例，再从 `%AppData%\HistoryVulcan\Modules\<模块名>`
删除完整运行包；失败时恢复原包。仅调用 `vulcan.module.unload` 只会移除当前内存快照，不删除磁盘包。
正常开发使用 `vulcan.dev.submit` 和 `vulcan.dev.finish`：二者在
候选构建、测试与提交成功后，把刚写入的工作区（或主树）版本化候选经活宿主
`vulcan.module.install` 安装并热重载。离线 CLI 写 AppData 不算热重载。
运行时状态以 `vulcan.module.list` 为准。

## 5. CLI 合同

同目录 `HistoryVulcan.Cli.exe` 是模块开发入口：

```text
HistoryVulcan.Cli.exe --cli vulcan.dev.start project=<项目> slug=<问题> agent=<AI>
HistoryVulcan.Cli.exe --cli vulcan.dev.submit name=<模块> msg=<说明> worktree=<工作区> [allowDirty=true]
HistoryVulcan.Cli.exe --cli vulcan.dev.finish name=<模块> msg=<说明> worktree=<工作区>
```

`--cli` 固定运行离线组合来构建和写 z，不会另起第二个宿主。`submit`/`finish` 的装包
再经 `--runtime` 同管道打到活宿主。开发三步只给模块，宿主自身使用
`vulcan.release.cycle`。`submit` 脏树默认拒绝，版本或源码改动已经确认时显式传 `allowDirty=true`；
`dryRun=true` 只报告计划，不构建、不写候选、不提交、不安装。
工作区 `submit` 覆盖同版本当前候选；主树正式促级仍拒绝内容不同的同版本覆盖。

脚本使用 `--format json` 时 stdout 只输出一个结果对象，字段固定为 `runId`、`success`、`exitCode`、
`executionTarget`、`candidatePath`、`installedPath`、`runtimeAck`、`logPath`、`diagnostics`、`data`。
`vulcan.module.list` 的模块名、版本、`instanceId`、`commandCount` 在 `data`。

`--runtime` 只用于查询已运行宿主的受限状态，或执行批准过的 `reload` / `install`；
模块开发的装包由 `submit`/`finish` 内部走同一条 `install`，不要单独调。
