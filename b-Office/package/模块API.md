# HistoryVulcan 模块 API

适用宿主：**6.0.0**（冻结线 v6.0.0）。本文定义模块接入与消费语义，工作区操作见[模块开发手册](模块开发手册.md)。宿主提供模块注册、命令总线和开发/发布管线；界面归 Aurora、工作台/快捷键归 Mercury、Web/MCP 归 Portunus。

## 1. 引用与兼容

模块**只引用契约程序集** `z-Publish/host/HistoryVulcan.Core.dll`，设 `<Private>false</Private>` 并在构建前验证目标存在；测试工程反过来设 `Private=true`。
不引用 `HistoryVulcan.Services` / `ServiceHost`，不向宿主程序集开放 internals。源码联调须显式开关，不自动回退到宿主 ProjectReference。
起步模板是模板仓 `0000-002-ModuleReady`：已按上述方式引用，派生后运行其实例化脚本即可。

**宿主给模块的只有「一条总线 + 一个上下文」**。Core 的公开类型就是下面这张白名单，宿主测试逐个核对，多一个即失败；
Services、ServiceHost 两个程序集没有公开类型。

| 类型 | 用途 |
| --- | --- |
| `IModuleContextAware` / `IModuleContext` | 模块入口与宿主给的上下文（§2） |
| `ICommandBus` / `ICommandRegistrar` | 窄总线：执行、安静执行、中途确认；只能加指令的登记口 |
| `IModuleLog` / `ModuleLogExtensions` / `ShellLogLevel` | 宿主那一份日志，只写 |
| `IModuleEnvironment` / `HostRunMode` | 数据目录、运行方式、宿主版本 |
| `BusEvent` | 总线事件 |
| `IFrontend` | 界面模块实现的前端角色 |
| `CommandDescriptor` / `ParameterSpec` / `ParamType` / `CommandLevel` / `CommandContext` / `CommandResult` | 指令定义、参数与回执 |
| `CommandParser` / `ParsedCommand` / `CommandSyntaxException` | 指令文本的解析与引号转义 |
| `ModuleDomainNaming` | 模块名 → 指令域的规则（主题前缀也按它） |
| `BuiltinCommandDefinitions` | 前端角色的共享指令定义（`vulcan.command.help`、`vulcan.app.get/set`） |
| `ModuleInfoBase` / `ModuleCommandAttribute` | 模块身份与反射指令元数据 |

### 6.0.0 迁移（主版本，二进制不兼容）

`IModuleContext.Bus`、`Log`、`RegisterCommands` 的签名变了，**5.x 编译的模块包在 6.0.0 宿主上一律接不上**
（`MissingMethodException`），全部模块须对 6.0.0 重新编译发布。源码层面：

| 已收回 | 现行方式 |
| --- | --- |
| `CommandBus`、`CommandRegistry` 具体类（`Registry`、`Validate`、`Executed`、`RemoteExecutor`、`ShouldUseRemoteCommand`、`UiContext`、`Confirmation`、`FormatUsage`、`GetMethod`、回显类别常量…） | `context.Bus` 是 `ICommandBus`，`RegisterCommands` 给 `ICommandRegistrar`；目录、校验、补全走只读指令（§2）；日志类别按 §2 的字符串约定 |
| `Register(descriptor, source)` 两参登记 | `Register(descriptor)`；来源由宿主按模块盖章 |
| `IShellLog`、`ShellLogEntry`、`ShellLogExtensions` | 写：`IModuleLog`（`Info/Warn/Error/Debug` 扩展）；读：订阅 `vulcan.log.entry`，补历史执行 `vulcan.log.recent` |
| `ISettingsService`、`IConfirmationService`、`AppIdentity`、`ApplicationIdentity`、`CommandClassLabels`、`DomainFocus` | 设置接口模块自持；确认用 `Bus.RequestConfirmation` 或 `IFrontend.Confirm`；宿主版本读 `Environment.HostVersion`；显示约定由界面自持 |
| Services 全部公开类型：`ModuleHost`、`RuntimeModuleDiscoverySource`、`ModuleDiscovery*`、`ModuleMeta`、`CommandCatalogRow/Detail`、`CommandParameterInfo`、`CommandDomainInfo` | 装载冒烟用 `HistoryVulcan.Cli.exe --probe`；Data 是 JSON（§3），按字段名读 |
| `diana.` 前缀的本机可信兼容 | 删除；模块调用一律带宿主盖的 `module:` 章 |

测试里要登记口或总线，自写一个 `ICommandRegistrar` / `ICommandBus` 替身（模板与各模块仓的 `ModuleTestHost.cs` 是现成写法）；
真实装载与执行以 `--probe` 为准。历史删除记录见宿主仓 `b-Code-Eng/public-api-baselines/<版本>/`。

## 2. 模块入口与上下文

| 成员 | 说明 |
| --- | --- |
| `Bus` | `ICommandBus`：`ExecuteAsync(text, source)` 执行（回显、确认、结果进宿主日志，发 `vulcan.command.executed`）；`InvokeAsync(text, source)` 安静执行（不回显、不记结果、不发事件，确认闸口照旧，取数用）；`RequestConfirmation(prompt)` 处理器执行中途要确认 |
| `RegisterCommands(Action<ICommandRegistrar>)` | 把本模块指令暂存进当前快照，提交时一并登记，随模块卸载撤销 |
| `Log` | 宿主那唯一一份日志，只写 |
| `Environment` | `ModuleName`、`DataDirectory`、`PackageDirectory`（6.0.0，本包槽位，只读）、`RunMode`、`HostVersion` |
| `Subscribe(topic, handler)` / `Publish(topic, payload)` | 总线事件 |
| `RegisterFrontend(IFrontend)` | 登记唯一前端 |

`Log`、`Environment`、`Subscribe`、`Publish`、`RegisterFrontend` 带默认实现：模块自写的上下文替身不覆盖时调用抛 NotSupportedException。

**来源由宿主盖章**：经 `context.Bus` 的调用，宿主把模块给的 `source` 包成 `module:<模块名>:<source>`（空串时为 `module:<模块名>`），
处理器看到的 `CommandContext.Source` 就是盖章后的值。模块在自己的处理器里再发指令时，传最里面那层标签即可，别把收到的整串原样转交（会叠章）。
运行包变更（`vulcan.module.install/remove/uninstall`）只接受本机可信来源：没有内层的 `module:<名>` 可信；有内层就按最里面那层判，
所以网关转进来的远端请求（`module:HistoryPortunus:MCP:<客户端>`）不会因为外面套了模块章就变成可信。

宿主先发现程序集，再逐个 Attach，成功一个才发布其命令。`dependsOn` 是模块名数组，只约束次序，例如 `{"dependsOn":["HistoryAurora"]}`；缺失、环或自依赖只记诊断。无依赖及环内按名称排序。

Attach 时只有此前成功模块的命令可见，不应在此跨模块调用或拉全量页面目录。需等全宿主完成的工作登记 `<域>.host.ready`：整轮结束经安静通道调用一次，不进历史；未登记跳过，单包热装不触发。`vulcan.module.ready` 可查询装载是否结束及未接入名单，不能以延时重试代替就绪信号。

Attach 失败时该模块全部命令不可执行；冷启动保留诊断并继续，热安装失败回滚。同内容包仅在 `Attached=true` 时是幂等成功，失败或未装载实例可重装修复。

### 前端登记

同一宿主只允许一个前端。另一模块已登记时 `RegisterFrontend` 抛 InvalidOperationException；同一模块重复登记替换旧登记，旧句柄释放不影响后继登记。释放返回的句柄、模块卸载、热装失败或 Attach 失败时宿主撤销登记，确认回到宿主缺省（拒绝）。

登记期间：`Level=Ask` 的命令由前端确认；`RequiresUiThread` 命令编组到前端的 `UiContext`（已在界面线程上时就地执行）；`vulcan.app.show / hide / close / focusconsole` 与 `vulcan.app.quit` 的关窗步骤转交前端的 `ExecuteAsync`。
前端自己的指令照常经 `RegisterCommands` 登记，要碰窗口的声明 `RequiresUiThread`；不要自建第二套总线或把指令抄成代理再登记。
`vulcan.command.help`、`vulcan.app.get/set` 是前端角色的共享定义（`BuiltinCommandDefinitions`），宿主已登记同名时前端不再登记。

### 宿主日志

`IModuleContext.Log` 是宿主那唯一一份日志：Bus 上每条指令的回显、进度与结果都写在这里并落盘，来自界面、CLI、MCP 还是模块嵌套调用都一样。
**控制台只显示这一份**，界面模块不得另建日志来显示命令输出；模块自己的运行日志也写进来（类别约定以本模块指令域开头）。

日志类别是契约的一部分（字符串，不是 C# 常量）：回显 `cmd:<来源>`、进度 `cmd:progress:<域>:<类>`、结果 `cmd:result:<域>:<类>`、
总线内部故障 `cmd:internal`、事件中枢诊断 `bus.event`。成功结果单行且不超过 200 字进 Info，多行或超长时 Info 只留提要、正文进 Debug；失败整条进 Error。

要让人看见长任务的过程，在处理器里写 `CommandContext.Progress`，不要等结束后在回执里汇总；经 Bus 调用别的模块时，对方的过程由对方自己写，调用方不复述。

### 统一契约速查

| 需要 | 用这个 | 不要 |
| --- | --- | --- |
| 执行指令、中途确认 | `context.Bus.ExecuteAsync` / `RequestConfirmation` | 自建总线或注册表 |
| 取数（不是操作） | `context.Bus.InvokeAsync` | 为了不刷控制台而绕过总线 |
| 可写数据目录 | `context.Environment.DataDirectory`（`%AppData%\HistoryVulcan\ModuleData\<模块名>`，装包/热装/卸载都保留，`uninstall purge=true` 才删） | 自己拼 `%AppData%` 路径、写进包槽位 |
| 随包发布的文件（外部可执行程序等） | `context.Environment.PackageDirectory`（只读，6.0.0） | 从宿主数据根推 `Modules\<名>`、用 `Assembly.Location`（内存流装载时为空）、往槽位里写 |
| 运行方式 | `context.Environment.RunMode`：`Service` / `OfflineCli`（`--cli` 与 `--export-command-manual`）/ `Probe` | 读进程名或进程参数 |
| 宿主版本、项目库、工作区根、运行区、宿主可执行文件 | 执行 `vulcan.host.info` | 按进程名找宿主、写死 `2026-023-HistoryVulcan/z-Publish` |
| 指令目录、单条详情、模块列表 | 执行 `vulcan.command.list` / `show`、`vulcan.module.list`，按 JSON 字段名读 Data（§3） | 强转宿主的 C# 类型 |
| 输入校验、未知指令建议、目录版本 | `vulcan.command.validate text=`、`vulcan.command.suggest name=`、`vulcan.command.revision` | — |
| 目录/模块/执行/日志变化 | `context.Subscribe`，见下表 | — |
| 通知别的模块 | `context.Publish("<本模块指令域>.<事件>", 载荷)` | 写文件让对方去读 |
| 装载冒烟 | `HistoryVulcan.Cli.exe --probe <包目录> --cli "<指令>" --format json` | 在测试里构造宿主类型 |

宿主发布的主题（载荷为 JSON，字段只增不减）：

| 主题 | 载荷 | 说明 |
| --- | --- | --- |
| `vulcan.catalog.changed` | `{ revision }` | 指令目录变了；去抖，一轮装载只发一两次 |
| `vulcan.module.changed` | `{ modules: [{ name, version, attached, commandCount }] }` | 模块清单变了（整轮重载、单包装卸都经这里） |
| `vulcan.command.executed` | `{ name, source, success, summary }` | 一条指令执行完；不含指令全文与 Data |
| `vulcan.log.entry` | `{ time, level, category, message }` | 宿主日志新增一行；**订阅方不要把它再写回日志**，否则绕圈 |

事件规则：主题是小写点分段、至少两段；订阅可用 `前缀.*`。每个订阅按发布顺序串行收到事件，处理器在线程池上运行，抛出的异常只记日志。
事件是通知不是调用：要细节就再执行只读指令。模块只能发布以自己指令域开头的主题，来源记为 `module:<模块名>`；模块卸载时它的订阅自动退掉。

`--probe` 把包复制到临时运行区装载（manifest `dependsOn` 里正式运行区已有的包一并复制），数据目录也在临时目录，`RunMode = Probe`；
输出是否接上、指令数、附着失败与发现诊断，给了 `--cli` 就再执行那条指令。退出码 0 接上且指令成功，1 没接上或指令失败，2 用法错误。

## 3. 载荷形状（宿主指令的 Data）

宿主自己的指令，Data 一律是 `JsonElement`，字段名驼峰；**契约是这里写的 JSON 形状**，字段只增不减，消费方按名取字段、忽略不认识的字段。
模块自己的指令 Data 由模块自定（同进程可传活对象，由调用方管理类型与生命周期）。

`vulcan.command.list` 的每一行（`show` 的 `command` 也是这一形状）：

```jsonc
{ "commandName": "janus.proj.list", "domain": "janus", "commandClass": "proj", "method": "list",
  "summary": "…", "example": "…", "parameterCount": 1,
  "source": "module", "sourceDetail": "HistoryJanus",        // 宿主自身为 "framework:service"，sourceDetail 为 null
  "dangerous": false,                                        // 级别为「询问」
  "requiresConfirmation": false, "requiresUiThread": false, "readonly": true,
  "hiddenReason": null, "allowUnspecifiedParameters": false,
  "parameters": [ { "name": "…", "type": "string|int|double|bool", "required": true,
                    "default": null, "position": 0, "allowedValues": [], "description": "…" } ],
  "annotations": { "ui.page": "…" } }
```

| 指令 | Data |
| --- | --- |
| `vulcan.command.show name=` | `{ command: <行>, parameters: [...], annotations: {...} }` |
| `vulcan.command.domains` | `[ { domain, count } ]` |
| `vulcan.command.validate text=` | `{ ok, name, usage, error? }` |
| `vulcan.command.suggest name=` | `[ "<指令名>" ]`，最多 5 条 |
| `vulcan.command.revision` | `{ revision }` |
| `vulcan.module.list` | `[ { moduleName, description, author, version, open, assemblyFile, commandCount, slot, ui, instanceId, sourcePath, manifestPath, attachFailures: [], dataDirectory, attached } ]` |
| `vulcan.host.info` | `{ version, runMode, libraryRoot, worktreeRoot, modulesRoot, moduleDataRoot, dataRoot, hostExecutable }` |
| `vulcan.log.recent count=` | `[ { time, level, category, message } ]`，与 `vulcan.log.entry` 同形，取最新的若干条 |

`vulcan.command.validate/suggest/revision`、`vulcan.log.recent` 对 MCP 隐藏，供前端与网关经总线调用。

## 4. 命令契约

业务命令用小写 `<域>.<类>.<方法>`；域取模块所有者名称去 History 前缀，如 HistoryJanus → janus。两段直接方法仍受支持，显式 CommandClass 优先。

命令声明 Name、CommandClass、Summary、Handler；有输入时给 Parameters 与 Example。Readonly 表达写入性，Level 表达确认，HiddenReason 表达隐藏原因；默认确认拒绝，前端登记后由前端确认。能在执行前决定的确认用 Level=Ask，执行中途才知道的用 `Bus.RequestConfirmation`，不要自取确认通道。写了 ConfirmPrompt 就必须 Level=Ask，否则登记时拒绝。

Annotations 由模块解释；活对象放 CommandResult.Data，由调用方管理类型和生命周期。处理器不直接操作宿主窗口、模块槽或源码目录。

总线固定当次命令定义及敏感值，在回显、历史、结果、异常和进度中脱敏（参数名以 token/password/secret/privatekey/connectionstring 结尾或为 code；`vulcan.app.set` 敏感键的值），执行中注销/替换不影响该请求。`InvokeAsync` 不回显、不入历史，Data 保留原始载荷但进度仍脱敏；需要原值时读 Data，不解析回执文本。模块自行写日志也须自行脱敏。

`vulcan.app.get/set` 接受通用非空 key/value，读取、列举和写入回执遮蔽敏感键；存储位置、既有文件和旧键不自动迁移或删除。

## 5. 模块包与装卸

候选在 `z-Publish/HistoryX-vX.Y.Z/`，包含 module.manifest.json、模块程序集/XML、SHA256SUMS 和 docs 消费文档。项目/版本 props、源 module.manifest.json、根 project.manifest.json 的身份版本须一致。

运行区固定为 `%AppData%\HistoryVulcan\Modules\<模块名>`，只发现直属完整包，不扫描裸 DLL。SHA 清单覆盖槽位里的全部文件，只有包内发布归档 history/ 不计入；artifact/docs/deps 不得放入 history/。模块不得手工拷 AppData 或改变发现根。
模块的可写数据只在 `ModuleData\<模块名>`（§2）。槽位是只读的：装包整槽替换、不带旧文件；模块往槽位里写任何东西，下一轮发现就会因清单不符判为坏包（6.0.0 起，槽位 `data/` 不再保留也不再豁免）。

安装先校验、同卷暂存、卸载同名实例、备份并替换，只将该包装回快照。失败恢复原包；恢复失败保留备份并返回路径；提交后清理失败只报告残留，不撤销提交。

`vulcan.module.unload` 只移除内存实例和命令；`vulcan.module.uninstall name=` 删除完整运行包，数据目录默认保留，`purge=true` 一并删除，失败恢复原包。运行状态查 `vulcan.module.list`。模块开发通过 submit/finish 热装，避免全量 reload 拆掉全部模块；离线写 AppData 不算活宿主热重载。

## 6. CLI 合同

使用与运行宿主同目录的 `HistoryVulcan.Cli.exe`：

| 通道 | 行为 |
| --- | --- |
| --cli | 固定离线组合（`RunMode = OfflineCli`），不启动第二宿主；语法和白名单校验在组合前完成 |
| --probe | 测试装载一个候选包（`RunMode = Probe`），见 §2 |
| --runtime | 只连接活宿主，不可达即失败；当前用户管道、实例身份和挑战握手，动作还须 --approve |

runtime 仅放行 `vulcan.module.list/ready/reload/install`，reload/install 要求批准；已注册的其他模块命令也不可借此执行。模块装包由 submit/finish 内部调用 install，日用流程见开发手册；宿主自身使用 vulcan.release.cycle。

`--format json` 使 stdout 仅含一个结果对象：runId、success、exitCode、executionTarget、candidatePath、installedPath、runtimeAck、logPath、diagnostics、data。模块名、版本、instanceId、commandCount 从 list 的 data 读取。

JSON exitCode 与进程退出码一致：成功 0，普通失败 1，语法/用法错误 2，runtime 不可达 3；离线命令失败若携带 ExitCode 则沿用该值。工作区候选可覆盖同版本，主树正式促级拒绝内容不同的同版本覆盖。
