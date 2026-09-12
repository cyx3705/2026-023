# HistoryVulcan 模块 API

适用宿主版本：**5.3.0（冻结基线仍为 `v5.1.2`）**。

本文件是模块作者唯一需要的宿主合同。HistoryVulcan 只提供模块注册器、命令总线和模块开发管线；
界面与传输能力由认领模块维护，不属于宿主模块 SDK。

5.1.2 起模块接入面冻结，但**冻结标签不等于「所有历史接口仍在」**：5.2 与 5.3.0 都经明确批准删除过
过期公开面。现行 API 以本版批准基线与 Unshipped 增删为准；编译不过时先查这一篇，再查
`b-Office/history/` 下对应版本的归档（5.2 移除清单在 `5.2.1-现行合同归档.md`，
5.3.0 的在 `5.3.0-修复与清理证据.md`）。删公开面走主版本规则。

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

模块业务命令采用三段式小写名称：`<模块域>.<类>.<方法>`。模块域去掉 `History` 前缀，例如
`HistoryPortunus` 使用 `portunus.*`，`HistoryJanus` 使用 `janus.*`。

两段名称作为域内无类直接方法仍受支持；显式 CommandClass 优先。

每条命令至少声明 `Name`、`CommandClass`、`Summary` 和 `Handler`；有输入时声明 `Parameters` 与 `Example`。
`Readonly` 表达是否写入，`Level` 表达是否需要交互确认，`HiddenReason` 表达不应出现在通用命令目录的原因。
命令处理器不直接读写宿主窗口、模块槽或宿主源码目录。

模块可用 `CommandDescriptor.Annotations` 表达模块自有元数据；宿主不解释该数据。需要活对象时放在
`CommandResult.Data`，调用方负责理解其类型与生命周期。

敏感值由总线按参数名判定并在回显、命令历史与结果里遮蔽，进度日志按本次命名/位置的敏感值一并过滤。
`InvokeAsync` 仍返回内部原始 `Data`，它不回显、不入历史——**要明文的消费方读 `Data` 或直接用设置接口，
不要去解析回执文本**。`vulcan.app.get` / `set` 是通用 key/value 设置入口，没有前缀白名单。

## 4. 模块运行包

运行包只安装到 `%AppData%\HistoryVulcan\Modules\<模块名>`。安装会先校验 manifest 和完整 SHA256 清单，
在运行区外同卷暂存，卸载同名模块，备份后替换，再只把这一包装回当前快照。
新包加载前从备份复制 data；提交前旧包和原始数据保留。接入失败恢复原包与原始数据；
回滚失败保留备份并返回恢复路径。提交后才清理旧包，清理失败只报告残留路径，不回滚已提交安装。
清单覆盖模块包的不可变载荷；`history/` 是归档目录，`data/` 是模块运行态目录，两者不计入 SHA256SUMS。
manifest 的可选字段 `dependsOn`（字符串数组，模块名）声明接入次序，见 2.1。
模块的 `artifact`、`docs` 和 `deps` 不得放在 `data/` 下，升级时宿主保留已有 `data/` 内容。
manifest 里宿主不认识的字段（例如旧版的 MCP 字段）被直接忽略；宿主不迁移、也不删除旧配置文件或键。

`Attach` 失败的模块**一条命令都不发布**：冷启动保留失败诊断并继续装其余模块，热安装失败回滚。
同一个包只有 `Attached=true` 才算健康的幂等成功——失败或未装载的实例可以直接重装修复。
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

`--runtime` 只用于查询已运行宿主的受限状态，或执行批准过的 `reload` / `install`：
管道只放行 `vulcan.module.list` / `ready` / `reload` / `install` 四条，**别的指令即便在模块里注册过
也不能经 runtime 执行**（例如 `portunus.mcp.*`，请按 Portunus 自己的已发布合同走它的管理入口；
不要假定宿主另有模块扩展协议）。模块开发的装包由 `submit`/`finish` 内部走同一条 `install`，不要单独调。

## 6. 起步示例

`b-Code-Samples/DemoModule` 是对齐本版宿主的最小模块：引用 Core、`ModuleInfoBase` 只申报身份、`IModuleContextAware.Attach` 里 `RegisterCommands` 登记三段命令，并带完整 `module.manifest.json`。不要再抄旧的反射全暴露、本地 `ModuleInfoBase` 副本或 `.panel.json`。

本仓示例用同仓 `ProjectReference` 锁住与现行 Core 一致。复制到编号模块仓库后，改为 HintPath 引用已发布的 `z-Publish/host/HistoryVulcan.Core.dll`，`<Private>false</Private>`。流程见[模块开发手册](模块开发手册.md)。
