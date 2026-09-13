# HistoryVulcan 模块 API

适用宿主版本：**5.3.0（冻结基线仍为 `v5.1.2`）**。

本文件是模块作者唯一需要的宿主合同。HistoryVulcan 只提供模块注册器、命令总线和模块开发管线；
界面与传输能力由认领模块维护，不属于宿主模块 SDK。

5.1.2 起模块接入面冻结。5.2 与本轮 5.3.0 经明确批准删除过期公开面，兼容变化见下文；
不能据冻结标签推断所有历史接口仍存在。三份 Shipped 不改写，现行 API 结合 Unshipped 增删和本版批准基线判断。

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

## 2.2 5.2 消费变更摘要（公开面移除）

5.2 移除以下成员。**这十条在宿主源码、测试与七个已部署模块的二进制里都是零引用**，
因此对现有模块没有迁移动作；列出来是为了让将来编译不过的人知道去处。

| 移除 | 替代 |
| --- | --- |
| `CommandBus.ShouldUseRemote` | `CommandBus.ShouldUseRemoteCommand`（多一个命令文本参数） |
| `CommandDescriptor.HasAnnotation(key)` | `bool.TryParse(descriptor.Annotation(key), out var v) && v`；注解语义本就由消费方定义 |
| `CommandRegistry.DomainsOf(names)` | 无。域由 `CommandRegistry.GetDomain` / `Domains()` 给出，不再按命令名前缀猜 |
| `CommandRegistry.LegacyDomain(name)` | `registry.GetDomain(name)` |
| `CommandRegistry.LegacyClass(name)` | `registry.GetCommandClass(name)`（未注册名走的正是同一条推导） |
| `CommandRegistry.LegacyMethod(name)` | `CommandRegistry.GetMethod(name)`（两者原本逐字相同） |
| `CommandClassLabels.IsNone(value)` | `string.IsNullOrWhiteSpace(value)` |
| `DomainFocus.WouldPrefix(...)` | `DomainFocus.Resolve(...)` 的返回值与输入不等，即表示拼了前缀 |
| `ModuleHost.FindModuleDomainConflicts(...)` | 无。模块域由 module owner 决定，见 `CommandRegistry.ResolveDomain` |

同批还有一项**不改签名的行为收紧**：指令文本解析失败时，回显会遮掉命令名之后的整段
（此前按正则逐个遮）。那种输入执行不了，遮全是唯一不需要判断的安全答案。

命令行面新增 `vulcan.module.ready`：5.1.3 加了这条就绪查询却漏进白名单，
现已补上，`HistoryVulcan.Cli.exe --runtime vulcan.module.ready` 可用。

## 2.3 5.3.0 消费变更摘要

本轮按用户指定的 5.3.0 执行公开面删除与行为收紧，保留 v5.1.2 标签。常规删除走主版本规则不变；
这是一项有范围的例外，不保证未知第三方二进制兼容。完整符号以 5.3.0 Unshipped 和批准基线为准。

| 删除 | 当前入口或归属 |
| --- | --- |
| ZModuleDiscoverySource 类型、构造、扫描与路径迁移方法及常量 | RuntimeModuleDiscoverySource；只认固定运行区完整包 |
| ModuleHost(string modulesDir, log) | ModuleHost(IModuleDiscoverySource, log) |
| ModuleHost.ChangeDirectory / ChangeDiscoveryRoots / ReloadConfirmedSources | 运行根固定，无切换或确认源入口 |
| ModuleHost.RequireConfirmedSources / EnableCommands | 无；所有有效接入模块统一登记命令 |
| ModuleDiscoveryEntry.McpExposure 及含该参数的构造函数 | 新构造函数只含 Name/Version/PackagePath/ManifestPath/ArtifactPath/Ui/DocsPath/DependencyPaths |
| ServiceComposer.MigrateLegacyMcpSettings / RegisterServiceMcpSettingCommands | 配置由宿主内部统一注册，不再提供 MCP 专用 API |
| ShellRelayConfirmation 类型和远程确认方法 | 默认拒绝为内部实现，界面安装 Bus.Confirmation |

旧 manifest 额外 MCP 字段直接忽略。宿主不自动删除旧配置文件或键，不从其他设置文件迁移值。
vulcan.app.get/set 保持 key/value 参数及原 service 配置存储位置，允许通用键；
读取、列举、写入回执遮蔽敏感值，取消 mcp.* 白名单。敏感数据消费方按自身合同直接使用现用设置接口，不依赖显示回执取明文。

runtime 仅保留 vulcan.module.list/ready/reload/install；portunus.mcp.status/start/stop 即便在模块中注册也不能经 runtime 管道执行。
Portunus 的管理入口按其已发布合同使用；不得假定宿主另有模块扩展协议。当前用户管道、握手与动作批准保持。

保留 FrontendExecutor、RemoteExecutor、ShouldUseRemoteCommand、现用模块管理、目录 DTO、设置和路径接口：
已部署 Aurora/Janus/Mercury 元数据及消费合同存在引用。它们是通用集成面，不是宿主网关实现。

Attach 失败不发布该模块任何命令，冷启动保留失败诊断并继续其他模块；热安装失败回滚。
相同包只有 Attached=true 才视为健康幂等成功；失败或未装载实例可以重装修复。
进度日志按本次命名/位置敏感值过滤；InvokeAsync 仍返回内部原始 Data，不回显、不入历史，其进度也须脱敏。

## 3. 命令契约

模块业务命令采用三段式小写名称：`<模块域>.<类>.<方法>`。模块域去掉 `History` 前缀，例如
`HistoryPortunus` 使用 `portunus.*`，`HistoryJanus` 使用 `janus.*`。

两段名称作为域内无类直接方法仍受支持；显式 CommandClass 优先。

每条命令至少声明 `Name`、`CommandClass`、`Summary` 和 `Handler`；有输入时声明 `Parameters` 与 `Example`。
`Readonly` 表达是否写入，`Level` 表达是否需要交互确认，`HiddenReason` 表达不应出现在通用命令目录的原因。
命令处理器不直接读写宿主窗口、模块槽或宿主源码目录。

模块可用 `CommandDescriptor.Annotations` 表达模块自有元数据；宿主不解释该数据。需要活对象时放在
`CommandResult.Data`，调用方负责理解其类型与生命周期。

## 4. 模块运行包

运行包只安装到 `%AppData%\HistoryVulcan\Modules\<模块名>`。安装会先校验 manifest 和完整 SHA256 清单，
在运行区外同卷暂存，卸载同名模块，备份后替换，再只把这一包装回当前快照。
新包加载前从备份复制 data；提交前旧包和原始数据保留。接入失败恢复原包与原始数据；
回滚失败保留备份并返回恢复路径。提交后才清理旧包，清理失败只报告残留路径，不回滚已提交安装。
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

## 6. 起步示例

`b-Code-Samples/DemoModule` 是对齐本版宿主的最小模块：引用 Core、`ModuleInfoBase` 只申报身份、`IModuleContextAware.Attach` 里 `RegisterCommands` 登记三段命令，并带完整 `module.manifest.json`。不要再抄旧的反射全暴露、本地 `ModuleInfoBase` 副本或 `.panel.json`。

本仓示例用同仓 `ProjectReference` 锁住与现行 Core 一致。复制到编号模块仓库后，改为 HintPath 引用已发布的 `z-Publish/host/HistoryVulcan.Core.dll`，`<Private>false</Private>`。流程见[模块开发手册](模块开发手册.md)。
