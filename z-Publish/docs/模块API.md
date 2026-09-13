# HistoryVulcan 模块 API

适用宿主：**5.4.0 / v5.4.0**。本文定义模块接入与消费语义，工作区操作见[模块开发手册](模块开发手册.md)。宿主提供模块注册、命令总线和开发/发布管线；界面归 Aurora、工作台/快捷键归 Mercury、Web/MCP 归 Portunus。

## 1. 引用与兼容

模块引用已发布的 `z-Publish/host/HistoryVulcan.*.dll`，设 `<Private>false</Private>` 并在构建前验证目标存在。源码联调须显式开关，不自动回退到宿主 ProjectReference。本仓 `b-Code-Samples/DemoModule` 是最小示例，复制到模块仓库后改为上述 HintPath 引用。

现行公开面以 5.4.0 Shipped 为准。5.2、5.3.0、5.4.0 经明确批准删除或收回过期 API，不保证历史二进制兼容；后续常规删除或改签须走主版本。迁移要点：

| 已移除面 | 现行方式 |
| --- | --- |
| 宿主总线的 FrontendExecutor、ConfirmationRouter；宿主总线上写 Confirmation / UiContext / RemoteExecutor / ShouldUseRemoteCommand（5.4.0） | 界面模块调用 `IModuleContext.RegisterFrontend(IFrontend)` 登记唯一前端；处理器执行中途要确认时调 `Bus.RequestConfirmation(prompt)`。模块自建的 CommandBus 不受影响 |
| ModuleHost 单参与四参 Attach（5.4.0） | `Attach(registry, bus)`；设置与数据目录由模块自持 |
| AppPaths、SettingsService、ShellLog、CommandHistory、CommandSelectionState、IDeferredStartupWork、CliExposurePolicy 及 ServiceHost 程序集全部类型（5.4.0） | 宿主内部实现，不再公开。数据根按 `%AppData%\<应用名>\` 约定自行拼接；设置实现 ISettingsService 自持 |
| ModuleHost.InstallPackage / RemovePackage / Uninstall / UiContext / ReloadCompleted（5.4.0） | 装卸走 `vulcan.module.*` 命令与开发管线 |
| ShouldUseRemote、HasAnnotation、公开 Legacy*/DomainsOf/IsNone/WouldPrefix 辅助方法 | 使用 ShouldUseRemoteCommand、现行分类/域解析；注解由消费方解释 |
| ZModuleDiscoverySource、旧目录构造/切换、确认源和 EnableCommands | 固定运行区完整包发现，统一模块注册 |
| ModuleDiscoveryEntry.McpExposure | 构造函数不再接受该参数；旧 manifest 扩展字段被忽略 |
| MCP 专用设置注册/迁移、ShellRelayConfirmation | 通用设置；网关管理走 Portunus 自有入口 |

精确删除记录保存在宿主仓 `b-Code-Eng/public-api-baselines/<版本>/` 的 PreFreeze 文件。Services 仍公开模块测试装载器——ModuleHost 构造、Attach、Start / Reload / Unload、Modules、DiscoveryDiagnostics、EnableUiModules / EnableFileWatching——连同发现记录、ModuleMeta 与命令目录 DTO，供模块 Smoke 走正式装载路径。

## 2. 模块入口与就绪

| 类型 | 用途 |
| --- | --- |
| IModuleContext | Bus 执行命令，RegisterCommands 注册命令，RegisterFrontend 登记前端 |
| IModuleContextAware | Attach 保存上下文并接入 |
| IFrontend | 界面模块实现：UiContext、Confirm、界面生命周期命令 ExecuteAsync |
| ModuleInfoBase | 声明模块名、版本和主类型 |
| CommandRegistry / CommandDescriptor | 命令定义、参数与元数据 |
| CommandBus / CommandResult | 执行及文本/结构化回执 |

IModuleContext 不提供设置、日志或数据目录；状态由模块管理，宿主能力用总线集成。不要实现已移除的 IUiModule / IShellUi* 或复制 ModuleHost。

宿主先发现程序集，再逐个 Attach，成功一个才发布其命令。`dependsOn` 是模块名数组，只约束次序，例如 `{"dependsOn":["HistoryAurora"]}`；缺失、环或自依赖只记诊断。无依赖及环内按名称排序。

Attach 时只有此前成功模块的命令可见，不应在此跨模块调用或拉全量页面目录。需等全宿主完成的工作登记 `<域>.host.ready`：整轮结束经安静通道调用一次，不进历史；未登记跳过，单包热装不触发。`vulcan.module.ready` 可查询装载是否结束及未接入名单，不能以延时重试代替就绪信号。

Attach 失败时该模块全部命令不可执行；冷启动保留诊断并继续，热安装失败回滚。同内容包仅在 `Attached=true` 时是幂等成功，失败或未装载实例可重装修复。

### 前端登记

`RegisterFrontend` 带默认实现，模块自写的 IModuleContext 测试替身无需改动，未覆盖时调用抛 NotSupportedException。同一宿主只允许一个前端。另一模块已登记时 `RegisterFrontend` 抛 InvalidOperationException；同一模块重复登记替换旧登记，旧句柄释放不影响后继登记。释放返回的句柄、模块卸载、热装失败或 Attach 失败时宿主撤销登记，确认回到宿主缺省（拒绝）。

登记期间：`Level=Ask` 的命令由前端确认；`RequiresUiThread` 命令编组到前端的 UiContext；`vulcan.app.show / hide / close / focusconsole` 与 `vulcan.app.quit` 的关窗步骤转交前端。宿主交给模块的 Bus 上，Confirmation、UiContext、RemoteExecutor、ShouldUseRemoteCommand 只读，写入抛 InvalidOperationException。

## 3. 命令契约

业务命令用小写 `<域>.<类>.<方法>`；域取模块所有者名称去 History 前缀，如 HistoryJanus → janus。两段直接方法仍受支持，显式 CommandClass 优先。

命令声明 Name、CommandClass、Summary、Handler；有输入时给 Parameters 与 Example。Readonly 表达写入性，Level 表达确认，HiddenReason 表达隐藏原因；默认确认拒绝，前端登记后由前端确认。能在执行前决定的确认用 Level=Ask，执行中途才知道的用 `Bus.RequestConfirmation`，不要自取确认通道。

Annotations 由模块解释；活对象放 CommandResult.Data，由调用方管理类型和生命周期。处理器不直接操作宿主窗口、模块槽或源码目录。

总线固定当次命令定义及敏感值，在回显、历史、结果、异常和进度中脱敏，执行中注销/替换不影响该请求。内部 InvokeAsync 不回显、不入历史，Data 保留原始载荷但进度仍脱敏；需要原值时读 Data 或设置接口，不解析回执文本。模块自行写日志也须自行脱敏。

`vulcan.app.get/set` 接受通用非空 key/value，读取、列举和写入回执遮蔽敏感键；存储位置、既有文件和旧键不自动迁移或删除。

## 4. 模块包与装卸

候选在 `z-Publish/HistoryX-vX.Y.Z/`，包含 module.manifest.json、模块程序集/XML、SHA256SUMS 和 docs 消费文档。项目/版本 props、源 module.manifest.json、根 project.manifest.json 的身份版本须一致。

运行区固定为 `%AppData%\HistoryVulcan\Modules\<模块名>`，只发现直属完整包，不扫描裸 DLL。SHA 清单覆盖不可变载荷，data/history 不计入；artifact/docs/deps 不得放入 data/history。模块不得手工拷 AppData 或改变发现根。

安装先校验、同卷暂存、卸载同名实例、备份并替换，只将该包装回快照。新包接入前复制旧 data；失败恢复原包及原始数据，恢复失败保留备份并返回路径；提交后清理失败只报告残留，不撤销提交。

`vulcan.module.unload` 只移除内存实例和命令；`vulcan.module.uninstall name=` 删除完整运行包及数据，失败恢复原包。运行状态查 `vulcan.module.list`。模块开发通过 submit/finish 热装，避免全量 reload 拆掉全部模块；离线写 AppData 不算活宿主热重载。

## 5. CLI 合同

使用与运行宿主同目录的 `HistoryVulcan.Cli.exe`：

| 通道 | 行为 |
| --- | --- |
| --cli | 固定离线组合，不启动第二宿主；语法和白名单校验在组合前完成 |
| --runtime | 只连接活宿主，不可达即失败；当前用户管道、实例身份和挑战握手，动作还须 --approve |

runtime 仅放行 `vulcan.module.list/ready/reload/install`，reload/install 要求批准；已注册的其他模块命令也不可借此执行。模块装包由 submit/finish 内部调用 install，日用流程见开发手册；宿主自身使用 vulcan.release.cycle。

`--format json` 使 stdout 仅含一个结果对象：runId、success、exitCode、executionTarget、candidatePath、installedPath、runtimeAck、logPath、diagnostics、data。模块名、版本、instanceId、commandCount 从 list 的 data 读取。

JSON exitCode 与进程退出码一致：成功 0，普通失败 1，语法/用法错误 2，runtime 不可达 3；离线命令失败若携带 ExitCode 则沿用该值。工作区候选可覆盖同版本，主树正式促级拒绝内容不同的同版本覆盖。
