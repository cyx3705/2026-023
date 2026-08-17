# AppShell 版本记录

> 3.0.0 起，`2026-023-AppShell` 是框架唯一项目和文档权威；020/022 只消费固定版本包。
> 0.1 至 0.7.x 保留下表历史，其中 0.7.x 已停更，仅用于回滚。
> 冻结后的致命修复必须通过 AppShell 演示宿主、OHS、WBall 与包隔离回归。

| 版本 | 日期 | 里程碑 | 说明 |
|---|---|---|---|
| 3.0.3（最终冻结） | 2026-07-29 | V3 冻结与主工作区比例收口 | 将左右或上下侧栏总上限从 80% 收紧到 50%，确保中央主工作区至少占对应轴的一半；加载历史布局时主动把同侧多个 `LayoutAnchorablePane` 合并为一个标签组，修复旧模块侧栏继续挤占中央区的问题。公开 API 不变。源码候选提交 `dcfc56f9`，正式发布提交 `278a0cd2`；正式包已提升到 `z-Package-AppShell`，OHS 2.7.9 与 WBall 已固定消费 3.0.3 并通过门禁，冻结标签为 `v3.0.3`。完整记录见 `AppShell_3.0.3_主工作区比例收口.md` 与 `AppShell_3.0_冻结执行证据.md` |
| 3.0.2（正式） | 2026-07-29 | Docking 侧栏收口 | 修复运行期 `RegisterWindow` / `IShellUiRegistrar.RegisterToolWindow` 使用 `DockSide.Right` 时为每个模块窗口新建独立右侧窗格的问题。运行期侧栏放置现在优先复用同侧现有 `LayoutAnchorablePane`，新窗口直接成为右侧标签；窗口复位和 `win.dock ... pos=right` 使用同一规则，右侧不存在时才创建新窗格。主窗口缩放不再被误采样为分隔条手势，左右或上下侧栏合计最多占 80%，并在加载时自动修复超配历史布局。无需模块声明 `DefaultTabTarget`，公开 API 不变。源码提交 `8cf1bffb`，正式包已提升到 `z-Package-AppShell`；完整记录见 `AppShell_3.0.2_模块注册窗口右侧合并整改.md` |
| 3.0.1（源码已推送） | 2026-07-29 | 默认最小能力整改 | 修正 3.0.0 包中 `ShellConfig.EnableMcp`、`EnableModules` 默认开启造成的隐式网关监听和模块扫描；两者改为默认关闭，消费方显式启用。将本地 `command.*` 与中央命令集从 MCP 网关解耦，保证 MCP 关闭时仍保留主工作区、命令目录和指令详情，同时不创建网关、治理存储、审计器或监听端口。完整审计和证据见 `AppShell_3.0_默认最小能力问题整改.md`；源码提交 `3b82f0d6` 已推送，未覆盖 3.0.0 正式归档，未正式发布、未冻结 |
| 3.0.0 | 2026-07-28 | 独立正式发布（待冻结） | FZ-00~06 完成：删除数据库服务、表窗口与 `db.*`；留痕改为文件治理；MCP/Web 端口按应用稳定派生并支持冲突顺延；修复旧布局恢复后的比例塌缩；公开 `DockSide.Center`，并以原生文档主区承载命令集/业务中央窗口，消除早期候选“0.01px 空文档 + 并排工具窗”导致的左侧收缩；命令集成为不可隐藏、不可浮动、不可移出中央的固定主文档；中央区始终显示自己的页面头和页面选择标签，业务中央页可稳定切换且不会被命令集自愈抢回焦点；普通四边工具页可拖入中央成为标签页、再拖回四边，并保留布局持久化与 owner 生命周期；前端建立连接时自动发布权威命令目录，服务端动态代理并支持定向中继；会话、缓存、队列和 WebSocket 消息均设上限。冻结审查进一步完成敏感命令脱敏与低权限日志隔离、会话来源编码、远程资源异步读取、服务重启互斥体接力、WebSocket 分片/坏帧恢复、鉴权前限流、原子设置和日志背压。四包统一为 3.0.0；以 `b-Office/package` 为消费合同编辑源，四份消费合同归档到 `b-Publish/docs/3.0.0`，四包不再重复携带 Markdown，`z-Package-AppShell` 只保留当前正式四包和精简复用说明。Public API 基线、Debug/Release、69 项框架测试、格式门禁、漏洞审计和隔离 PackageSmoke 的结果见执行证据；020/022 均完成包消费迁移；仓库内本地正式 feed 获准提升，`v3.0.0` tag、NuGet.org 发布和 push 延后执行 |
| 0.7.2（停更，仅回滚） | 2026-07-27 | 统一工具窗口模型 | 删除 `WorkPageDescriptor`、工作页注册接口、`page.*`、固定主内容和文档标题壳；中央区仅保留无标签空背景，模块 UI 全部通过 `RegisterToolWindow` 注册普通窗口。最大化、拖动、浮动、停靠、隐藏和 owner 回收统一走工具窗口链；旧布局中的文档节点被丢弃，已注册工具窗尽量恢复。SE2SW 2.2.0 完成真实迁移。该版本为源码及二进制破坏性修订，旧 UI 模块必须重新编译。OHS Debug/Release 构建与八套 Smoke、SE2SW Release Smoke 通过；四包 staging、漏洞审计、隔离 PackageSmoke、演示发布及正式本地 feed 全绿，正式 manifest 指向洁净源码提交 `52694de2`。部署证据现归档于 `b-Publish/changelog/` |
| 0.7.1 | 2026-07-27 | 页面最大化与布局载入补强 | 工作页和工具窗支持双击蓝色标题拖动条最大化/恢复，并新增 `win.max`、`win.restore`；最大化期间标题条保留，恢复入口不会随标签消失。主内容与模块工作页统一显示标题条，模块工作页默认 `CanFloat=true`，可拖出、浮动并重新停靠；标题条在 `Loaded` 和可视父级变化时重绑 AvalonDock `LayoutItem`，保证首次装载、最大化重建与重新停靠后的拖动链。补齐工作页布局反序列化分支，载入布局时不再丢失已注册页面。Debug/Release 0 警告 0 错误；四包隔离 staging、漏洞审计、PackageSmoke 与演示发布全绿 |
| 0.7.0 | 2026-07-27 | 模块内嵌窗口与中央工作页 | 新增 `IShellUiRegistrar` / `IShellUiAware` 与 `WorkPageDescriptor`；`DockingHost` 支持运行期工具窗和工作页注册、owner 兜底回收、迟到模块近似布局恢复及 `page.list/open/close/activate`。模块热重载连续 10 次无页面重影，坏模块未主动注销时旧 ALC 仍可回收且 DLL 可删除；OHS 2.7.0 以 SE2SW 2.2.0 中央工作页完成真实集成 |
| 0.6.1 | 2026-07-26 | 会话与 Web 硬化 | `ClientSession` 按 MCP/Shell/Web 隔离客户端身份，`Mcp-Session-Id` 显式会话与连接降级并存，清偿实例级 `_clientName` 串话；协议清单严格限定 `2025-06-18`/`2025-03-26`，未知 initialize 版本回落最新版、无效请求头 400、缺头按旧版兼容。正式 WebGateway 提供 command/health/commands/events/confirm，落实 token、绑定、CORS、限流和远程确认安全缺省。四包 0.6.1 staging、漏洞审计、隔离 PackageSmoke 全绿 |
| 0.6.0 | 2026-07-26 | 服务宿主与 UI 模块 | 新增第四工程/包 `AppShell.ServiceHost`：无窗 WPF Application、单实例 mutex、服务确认、延迟启动、可注入用户级 Run 管理与 `svc.*`。命令描述符新增 Frontend 执行站点，总线支持服务/前端双向路由；Services 新增 HTTP/WS 前端接入点与 Shell 客户端。模块清单支持 `ui:true`，`IUiModule` 在 Dispatcher 上创建，热重载先销毁 UI 再卸载；既有 ShellWindow 单进程路径保持默认行为 |
| 0.5.0 | 2026-07-26 | 首次固定版本包 | 建立 `OneHistory.AppShell.Core/Services/Shell` 三层 NuGet 包与 win-x64 framework-dependent 演示 ZIP；统一 0.5.0 版本真值、中央依赖与锁文件，补齐 SourceLink、XML 文档、符号包、package validation、隔离 PackageSmoke、漏洞审计、manifest 和 SHA-256 发布链。`Microsoft.Data.Sqlite` 升至 8.0.29，并以 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4 清除旧 `lib.e_sqlite3` 2.x High 公告链；AvalonDock 保持 4.72.1。命令手册与公开 MCP 示例完成通用化，独立宿主按能力隐藏 OHS `tool.*` 按钮；设置/日志/MCP 持久化改用 invariant 格式。收录已验收的 MCP `structuredContent` 加法兼容实现，旧 `content` 保持。AppShell Debug/Release 0 警告 0 错误，隔离包消费与演示 GUI 通过，OHS Debug/Release 与六套 Smoke 全绿。0.5.0 仅发布仓库内本地 feed；OHS 继续 `ProjectReference`，未推送 NuGet.org、未创建 tag |
| 0.4.4 | 2026-07-24 | 派生反哺(大) | OneHistoryStudio V2.4 独立化前的最后一次反哺:把 MCP 服务、模块注册器、提示词治理三大件上抛为框架自带能力(默认启用,ShellConfig 可关),从此**派生应用一建立即有前端 + MCP 服务 + 模块注册器**。① 新增 `Core\Mcp`(McpExposurePolicy/CommandSchemaExporter/CommandManualGenerator/McpConfirmationScope/PromptTextIntegrity/**IMcpAuditLog** 契约)、`Core\Data\SqlText`、`Core\AppIdentity`(取入口程序集,可 `Use()` 覆盖);② 新增 `Services\Mcp`(McpGateway/PromptGovernanceStore/**McpAuditRecorder** 缺省留痕)、`Services\Modules`(ModuleHost/ModulePanelSync);③ 新增 `Shell\Mcp`(McpCommands/CommandCatalogCommands/PromptGovernanceCommands/RemoteConfirmDialog)、`Shell\Modules`(ModuleCommands),由 ShellWindow 在内置指令后、派生指令前自动装配并自管生命周期。**解耦改造**:McpGateway 原依赖派生侧 HistoryRecorder,抽 `IMcpAuditLog` 接口断开;ModuleHost 原直取 `Application.Current.Dispatcher`,改注入 `SynchronizationContext`(§14.2 分层),Services 层因此零 WPF 依赖;McpExposurePolicy 只读白名单清空 14 条 OneHistory 专有指令,改 `RegisterReadonly()` 由派生登记(框架基线只含框架自注册指令)。**同批并入体积治理**:新增 `Directory.Build.props` 钉死 win-x64 RID,单份输出约 27M→3.3M。模板独立构建(含自带 App 演示)0 警告 0 错误;派生侧 OneHistoryStudio 换用后主解决方案 Debug/Release 0 警告、五套冒烟 10/10 PASS、三目录逐文件哈希 0 差异 |
| 0.4.3 | 2026-07-16 | 派生反哺 | OneHistoryStudio V2.1.2~V2.1.5 期间产生、V2.1.6 质量整备(Q16-M1)审阅后整体回灌五文件:①CommandRegistry——Register 增 source 溯源(默认 "framework",兼容旧调用)+ GetSource + Changed 事件,Unregister 同步清理并触发;②CommandParser——指令名放宽为多段(域.动作.子动作…);③CommandBus——ExecuteAsync 增可选 CancellationToken,取消返回「指令已取消」;④DockingHost——首建布局默认比例种子保护 + 比例施加覆盖停靠组全部成员(比例语义修正);⑤BuiltinCommands——help 按域分组计数/列宽 24/详情补参数类型与安全·线程提示。逐条审阅确认均为通用能力,无派生应用专有逻辑;模板独立构建 0 警告 0 错误 |
| 0.4.2 | 2026-07-15 | 派生反哺 | 由首个派生应用 OneHistoryStudio(V2-M2/M3)回灌两处修正:①控制台多行日志逐行拆分入列表 + 滚动单位 Item→Pixel,修复「底部长日志显示不全」(ConsoleRow/ConsoleView,谨慎区,验收 8 于派生侧复跑通过);②CommandRegistry 新增 Unregister(name)——模块热重载场景下线指令域所需,调用方只应注销自己注册过的名称,Register 冲突即抛的规则不变(§5.3) |
| 0.4.1-M4 | 2026-07-12 | M4 修补 | 修复 x64 回收站删除闪退(SHFILEOPSTRUCT 误用 Pack=1,详见下方 M4 要点);资源窗口新增“打开文件夹…”与“恢复默认工作区”(res.root 的图形入口) |
| 0.4.0-M4 | 2026-07-12 | M4 | 控制窗口群(JSON 面板)+ 资源窗口 + res.*/panel.* 指令组。验收 5 / 6 达成;R-06 越界拒绝实测。当时使用复制底座模式下的二次开发权限规则；现行包消费方式见 `AppShell升级手册.md` 与 `AppShell_API与指令手册.md` |
| 0.3.0-M3 | 2026-07-12 | M3 | SQLite 数据服务 + 表窗口 + db.* 指令组。验收 4 / 9 达成(UI 单元格编辑手势留人工复验),N-04 十万行深页 13ms |
| 0.2.0-M2 | 2026-07-12 | M2 | 指令核心(解析/注册表/总线)+ 控制台窗口 + 正式日志服务。验收 7 / 8 达成,win.*、layout.* 可用 |
| 0.1.0-M1 | 2026-07-12 | M1 | 停靠二次封装 + 主窗口占位页 + 布局持久化。验收 1 / 2 / 3 / 10 达成(3 的多显示器混合 DPI 场景待人工复验) |

## 技术基线

- .NET 8(net8.0-windows)+ WPF + Dirkster.AvalonDock 4.72.1 + VS2013 Light 主题
- .NET 10 迁移预案见需求文档 §14.1

## 本机构建注意事项

1. **CET 兼容**:本开发机(Windows 10 LTSC 2021)对 CET 支持不全,.NET 10 SDK 自带的
   Roslyn 编译器进程会以 "Your Windows doesn't fully support CET" 崩溃。
   用 dotnet CLI 构建前需设置环境变量:`DOTNET_EnableWriteXorExecute=0`
   (仅影响编译器进程启动;可写入用户环境变量一劳永逸)。
   Visual Studio 内置 MSBuild 不受影响。
2. **NuGet 源**:机器全局配置里的 `https://nuget.cdn.azure.cn` 镜像已停服,
   本解决方案根目录的 `nuget.config` 已改为仅使用 nuget.org。

## M4 要点(维护者须知)

- **控制窗口群**:PanelManager 从 `<数据目录>/panels/*.json` + ShellConfig.Panels(C# 通道)
  加载 PanelDefinition,每个面板注册为独立可停靠窗口(窗口名 = 面板 id,win.*/layout.* 直接可用)。
  按钮点击 = 收集控件值 → `{控件id}` 填入指令模板 → 总线执行(验收 6)。
  八类控件见 Core/Panels/PanelControl;panel.set 反向驱动;panel.reload 原地重建(新增面板需重启)。
- **资源窗口**:懒加载单树;全部写操作生成 res.* 指令经总线;边界校验与回收站语义统一在
  Services/WorkspaceService(`res.mkdir path=..\x` 会被拒,已实测);FileSystemWatcher 500ms
  去抖自动刷新;双击打开可被 ShellConfig.OnResourceOpen 接管(R-03)。
- res.delete / db.update / db.delete(无 where)共用总线 ConfirmPrompt 拦截器 —— 危险操作单闸口。
- **x64 P/Invoke 教训(0.4.1 修复)**:SHFILEOPSTRUCT 绝不能带 `Pack = 1`(网上流传的 32 位写法)。
  x64 下会字段错位 → Shell 回写越界 → 栈损坏闪退,且 AccessViolation 无法被 .NET 捕获,
  总线的异常兜底(N-05)拦不住。新增任何 P/Invoke 结构体都要核对 64 位布局。
- **历史二次开发注意**:当时的冻结区 / 谨慎区 / 自由区用于源码复制模式；现行包升级与扩展规则见 `AppShell升级手册.md` 和 `AppShell_API与指令手册.md`,
  接手前必读。

## M3 要点(维护者须知)

- **数据抽象在 Core/Data/IDataService**(D-02 提供者接入位),SQLite 实现在
  Services/SqliteDataService(Microsoft.Data.Sqlite 8.0.10)。库文件在 data/ 下,
  连接经 `RegisterConnection(name, file)` 注册,缺省连接名 main。
- **行定位用 rowid**:查询固定 `SELECT rowid AS __rowid__, *`,表窗口据此拼
  `where="rowid=N"` 做编辑/删除,无主键表同样可编辑;WITHOUT ROWID 表自动退化只读。
- **表窗口是 db.* 的图形外壳**(验收 4 的机制):一切用户动作(翻页/筛选/编辑/删除/导出)
  都生成指令文本经总线执行;数据变更再经 `IDataService.DataChanged` 事件驱动表窗口
  自动重载(200ms 去抖)。手输指令与 UI 操作因此天然双向同步。
- **危险操作单闸口**(T-08/验收 9):无 where 的 db.update/db.delete 由总线 ConfirmPrompt
  拦截,UI 路径(“按筛选删除”空筛选)与手输路径走同一拦截器;“删除选中行”另有 UI 侧确认。
- **where/order/set 是原始 SQL 片段**(§5.1 语义),不做注入防护(面向使用者自己的库);
  表名过 sqlite_master 存在性校验。
- 演示数据:首启建 users 表(1200 行);`debug.seedbench rows=100000` 生成 bench 大表
  (N-04 实测:十万行第 180 页查询 13ms)。
- **已知限制**:多实例并发时第二实例抢不到日志文件(静默丢文件日志,内存/控制台不受影响);
  单进程单实例开关为 N-07(P2)。

## M2 要点(维护者须知)

- **指令核心在 AppShell.Core/Commands**(零 WPF 依赖):CommandParser(§5.1 语法,引号/转义/注释)、
  CommandRegistry(注册冲突即抛,未知指令给相近候选)、CommandBus(校验 → 二次确认拦截 →
  执行 → 回显;异常全捕获不崩溃;RequiresUiThread 的指令经 SynchronizationContext 编组)。
- **回显与日志共用 IShellLog 管道**(L-03):回显类别 `cmd:<来源>`,结果 `cmd:result`,
  进度 `cmd:progress`;控制台按类别前缀渲染成附录 C 样式。布局手势指令(W-10)也走
  `cmd:layout` 类别,可在控制台一键屏蔽。
- **控制台承压路径**(N-03/验收 8):日志事件 → 并发队列 → 100ms 批量刷入
  RingCollection(追加发单条 Add,裁剪发 Reset,头部偏移摊销 O(1));实测 3000 条/秒
  持续 20 秒(60k 条,击穿 50k 上限走裁剪路径)UI 不冻结,文件零丢失。
- **控制台窗口内容由 Shell 接管**:描述符 Id="console" 可不设 ContentFactory,位置仍由
  派生应用声明;未声明时 Shell 强制注册(架构不变量 2)。
- **菜单项点击 = 发指令**(S-02):ShellWindow 菜单一律 `bus.ExecuteAsync(text, "UI")`。
- **启动参数 `--exec "<指令>"`** 可重复,启动后按序执行(来源 脚本:startup),自动化/自测入口。
- **派生应用注册指令**:`ShellConfig.ConfigureCommands = registry => registry.Register(...)`,
  示例见 App 的 debug.logflood(异步长任务 + Progress 上报范式)。
- 手动输入交互(↑/↓ 历史、Tab 补全、多行粘贴拆分、Ctrl+` 聚焦)属 UI 键盘路径,
  无法无头自动化,发布前人工过一遍。

## M1 封装要点(维护者须知)

- **AppShell.Shell 是唯一引用 AvalonDock 的项目**(§14.2 封装原则),派生应用只面向
  `AppShell.Core.Docking.IDockingService` 与 `ToolWindowDescriptor`。
- **标签条置顶(W-04)**:AvalonDock 窗格样式经 `DockingManager.AnchorablePaneControlStyle`
  属性下发(主题字典中的隐式 Style 不会命中)。ShellWindow 以主题键
  `AvalonDockThemeVs2013AnchorablePaneControlStyle` 取基底样式做 BasedOn,仅替换模板
  (标签行移至顶部)。**升级 v5 时该资源键会变,需同步调整。**
- **比例语义(W-05)**:AvalonDock 对"与文档区同面板的侧窗格"是像素语义
  (`LayoutPanelControl.OnFixChildrenDockLengths` 会把星值固化为像素,窗格未排布时
  会固化成最小值 25px)。DockingHost 维护每窗口目标比例,在首次排布后与主窗体缩放后
  (SizeChanged 去抖 200ms)按比例重新施加像素尺寸;恢复布局后首次改为反向采集比例。
- **布局手势 → 指令(W-10)**:监听 LayoutRoot.Updated,去抖 500ms 后对全部窗口状态
  做快照差分,输出 win.show/hide/float/dock/ratio 等价指令;程序化变更经 Suppress()
  抑制并重建基线,防再入回声。
- **布局损坏回退(N-06)**:反序列化异常 → 删除损坏文件 → 构建默认布局 → Warn 告警
  (占位页横幅可见,M2 起进控制台)。

## HistoryVulcan 3.11.4 and Earlier Validation Records (archived 2026-08-17)

- 2026-08-15 本地（候选模块界面试用与后台中继，REQ-MOD-004，版本线推进到 3.11.4）：Debug 构建 0 error，
  外置测试 **202/202 通过、3 跳过**（新增 `ModuleTrialUiTests` 6 个用例）；`dotnet format --verify-no-changes`、
  质量门禁（0 抑制 / 0 热点）与公开 API 基线通过。
  **版本线**：3.11.3 已发布到正式 z，本次新增了公开面与两条命令，因此推进到 3.11.4 而不是覆盖同号发布——
  同号不同内容会让管线的身份对齐失去意义。`VulcanVersion.props`、`project.manifest.json`、AGENTS.md
  与新的 `eng/public-api-baselines/3.11.4/` 一并对齐。
  **API 门禁修正**：3.11.3 批准基线此前缺 `ZModuleDiscoverySource` 的
  `AutomaticRootNotFound` / `DefaultLibraryRoot` / `LegacyVestaLibrary` / `CoerceConfiguredRoot` /
  `ResolveAutomaticRoot` 五项——它们在 1cb6ada 进入 Unshipped 时未同步批准基线，门禁自那时起即为红；
  本次连同新增的 `ModuleHost.LoadTrialUi` / `UnloadTrialUi` 一并补齐，基线改动纯追加。
  **锁定还原修正**：入库的五份 net8.0 库 `packages.lock.json` 带有 `net8.0/win-x64` 节，
  CI 首步 `dotnet restore --locked-mode`（不带 `-r`）因此以 NU1004 失败；该节由
  `Build-HistoryVulcanPackage.ps1` 的 `-r win-x64 --force-evaluate` 在本机生成，不应入库。
  移除后 `--locked-mode` 还原通过，候选构建仍按原方式自行 force-evaluate。
- 2026-08-16（3.11.4 正式发布）：Diana 管线 `Publish-OneHistoryModule.ps1 -Module HistoryVulcan -Publish`
  跑通全部宿主门禁——严格宿主合同、Release 单元测试 202/202（3 跳过）、质量门禁、3.11.4 公开 API 基线；
  正式宿主进程按正式 EXE 绝对路径停止后原子提升 `z-HistoryVulcan`，旧 3.11.3 归档到
  `b-Publish/history/HistoryVulcan/3.11.3-20260815-164759-4608b9f8`，登录启动项已按新正式路径修复。
  快照 `manifest.json` 为 3.11.4、`host/HistoryVulcan.exe` 文件版本 3.11.4.0，
  `SHA256SUMS` 46 条与磁盘 46 个文件逐项独立复算一致；已确认发布出去的
  `host/HistoryVulcan.Shell.dll` 内含 `trialui` 命令名。`sourceDirty=true` 属先部署后提交的预期状态。
  **发布链修正**：`eng/release/consumer-docs.json` 仍登记 ba25b01 删掉的
  `HistoryVulcan_模块与MCP接入.md`，导致快照 `manifest.json` 的 `documents` 谎报一份并不存在的文档
  （`docs/` 目录本身是照实拷的，两者对不上）；`project.manifest.json` 的 `documents.moduleAndMcp`
  指向同一份已删文件，直接让严格宿主合同失败。两处一并移除后重新促级，manifest 现与 `docs/` 一致。
  **z docs 入库（DEC-041）**：`z-HistoryVulcan/docs/` 由管线生成、被已入库的 `SHA256SUMS` 覆盖，
  目录本身却自 c9d2e90 起不入库——新克隆的仓库里校验清单必然指向一批不存在的文件。
  c9d2e90 的前提（Diana 集中托管消费文档）已随集中副本区退役而消失，本轮按决策回归把该目录重新入库，
  与 `z-HistoryDiana/docs` 一致；跨项目读取仍走 `diana.docs.vulcan`。
  **人工验收**：用户已在重启后的 Vulcan 3.11.4 上完成界面验收，消费文档同步到位。

- 2026-08-12（3.11.2 参数补全正式发布）：Diana 正式管线完成候选重建、Release 构建与完整测试 196/196；
  `FocusedConsoleCompletesDomainClassMethodAndParameter` 覆盖参数名 → 注册允许值 → 第二个参数名 → 第二个参数值并在末层关闭，
  `PositionalParameterHintThatDoesNotChangeTextClosesWithoutRefreshing` 覆盖无文本/光标进展时关闭且不再次请求补全。
  格式、严格项目合同、质量门禁与 3.11.2 四程序集公开 API 基线通过；API 基线与 3.11.1 同形，
  Core、CommandBus、解析器和注册协议均未修改。3.11.2 候选 EXE 文件版本为 3.11.2.0，
  正式 Z 的 `HistoryVulcan.exe` 文件版本为 3.11.2.0，`SHA256SUMS` 40/40 独立复验一致；
  manifest 记录干净来源，旧 3.11.1 快照已归档，登录启动项已指向 3.11.2 正式路径。

- 2026-08-12（3.11.1 正式发布）：Diana 以显式脏源授权重建宿主候选，宿主合同、Release 195/195 测试、质量门禁和 3.11.1 公开 API 基线全部通过；正式 Z 与消费文档镜像原子提升，40 条 `SHA256SUMS` 独立复验通过，旧 3.11.0 已归档。登录启动项按新正式路径修复；从 `z-HistoryVulcan/host/HistoryVulcan.exe --focus-console` 冷启动后，后台服务端点与带窗口前端均就绪并从同一正式路径运行。

- 2026-08-12（3.11.1 源码候选）：项目合同、Release 构建（0 warning/error）、完整 Release 测试 195/195、格式、质量和 3.11.1 四程序集公开 API 基线通过。首次 API 门禁明确报告缺少 3.11.1 批准目录；新增同形版本基线后，Core/Services/ServiceHost 保持既有累计面，Shell 仅追加 `CommandCatalogDetail.Annotations` 两个访问器，重跑通过。普通布局不弹 Popup/不吞 Tab、聚焦控制台连续补全和 `vulcan.command.show` 注解回归均通过。候选宿主 manifest 为 3.11.1，40 条 `SHA256SUMS` 全部独立复验；Core 与 CommandBus 未修改。

- 2026-08-11（3.10.1 正式发布）：项目合同、3.10.1 公开 API 基线、质量门禁、
  `dotnet format --verify-no-changes` 与 `git diff --check` 通过；3.10.0/3.10.1 四份基线 SHA-256 完全一致。
  隔离 Release 构建 0 warning/0 error，外置测试 193/193 通过，其中普通布局回归确认首字母保留控制台
  焦点，Tab 连续推进域、类、方法和参数。候选宿主已生成到 `b-Publish/current`，版本 3.10.1；
  Diana 正式管线随后通过 193/193 测试并原子提升 Z 与消费文档镜像；旧 3.10.0 已归档。
  首次提升被运行中的正式宿主锁定，在移动前失败；精确停止前端与服务后完整重跑成功。

- 2026-08-11 本地（3.5.0 候选部署审计）：`Build-HistoryVulcanPackage.ps1` 成功生成候选宿主（3.5.0.0、
  41 个 manifest 载荷文件、5 份消费文档、SHA-256 覆盖 42 个文件），但 `sourceDirty=true`，未提升正式 Z。
  候选后台注册 94 条指令；四模块 64 条（Diana 10、Janus 32、Mercury 19、Minerva 3）。真实 MCP
  `standard` 目录 68 条（Vulcan 17、Diana 10、Janus 21、Mercury 17、Minerva 3），`readonly` 目录
  47 条（Vulcan 11、Diana 9、Janus 21、Mercury 3、Minerva 3）。`diana_kit_now`、`janus_status`、
  `mercury_app_status` 实调通过；`HistoryMinerva_Status` 进入模块但报告缺少 `HistoryMinerva.Worker.exe`，
  因此本轮候选审计为**部分通过，Minerva 运行时阻塞**。`PackageSmoke` 因依赖未发布的本地 NuGet 包报 NU1101，
  不纳入 3.5.0 宿主快照门禁。Codex CLI 临时 MCP 配置可识别，但因本机 API Key HTTP 401 未完成原生模型工具调用。

- 2026-08-11 本地（3.5.0 MCP 双进程与命令总线健康专项）：定向 Debug 测试 **8/8**；Release 构建
  0 warning、0 error，完整 Release 测试 **186/186**；`dotnet format --verify-no-changes`、公开 API
  基线、项目合同（`-Instantiation`）、质量门禁和 `git diff --check` 均通过。使用一次性临时宿主加载当前
  正式 Z 模块后，Diana、Janus、Mercury、Minerva 四模块成功装载，共登记 64 条模块指令；真实 HTTP
  `tools/list` 返回 62 条可见工具，覆盖四个模块域，并以 `tools/call` 成功执行只读命令
  `HistoryMinerva_Status`。模块注销/重载后工具目录动态变化且 MCP 监听实例和端口不变。当前正式
  Minerva 快照的工具名仍为 `HistoryMinerva_*`，属于历史命名残留，不是注册缺失；本轮未改名、未更新
  正式 Z、未提交或发布。

- 2026-08-11 local (3.5.0 candidate): project contract, `dotnet format --verify-no-changes`,
  `git diff --check`, and the 3.5.0 public API baseline gate passed. After the running Z host processes were
  stopped, standard Release build completed with 0 warnings and 0 errors, and standard Release tests passed
  **178/178**. During lock triage, an isolated `artifacts/hv-check1/bin` build/test also passed; the first temp
  test run outside the repository had one expected path-discovery failure
  (`ModulesViewUsesOneRefreshActionAndNoCommandDetailPane`) because it finds the repository root from
  `AppContext.BaseDirectory`. The `win-x64` runtime sections were regenerated into package lock files with
  `dotnet restore ... -r win-x64 --force-evaluate`, then `Build-HistoryVulcanPackage.ps1` created the 3.5.0 host
  snapshot at `b-Publish/current` with manifest and SHA-256 inventory. No formal Z snapshot or publication was
  performed.

- 2026-08-10 本地（快捷键热重载生命周期与延迟标签目标修复）：Release 测试 **176/176**；新增
  `ModuleHostContextTests.ReloadReplacesOwnedGlobalShortcutsWithoutConflictOrLeak` 和
  `DockingContractTests.RuntimeRegistrationResolvesTabTargetDeclaredAfterFollower`，并通过格式、质量门禁、
  项目合同和公开 API 基线。旧快照未重新正式发布，待 Diana 宿主发布流程批准后更新 Z。

- 2026-08-10 本地（3.4.0 Diana 候选管线）：`Build-HistoryVulcanPackage.ps1` 先以
  `NuGetAudit=false` 锁定还原 win-x64 资产，再生成宿主候选；递归 SHA 校验、Release 单元测试
  174/174、零抑制/零热点质量门禁和 3.4.0 公共 API 基线全部通过。未执行 `-Publish`，未更新正式 z。

- 2026-08-09 正式发布（3.3.0）：Publish-HistoryVulcanHost.ps1 -Version 3.3.0 -DeployToZ 已部署到 z-HistoryVulcan；HistoryVulcan.exe 3.3.0.0，sourceCommit=24f21976，sourceDirty=false；候选与正式 SHA-256 校验通过。

- 2026-08-09 本地（3.3.0 指令三段式与快捷键/命令工作台外置）：内置命令硬切 `vulcan.<类>.<方法>`，
  命令集列域|类|方法；`GlobalShortcutService` 与 CommandSurface 迁至 HistoryMercury 4.1.0；宿主保留
  Core 合同与控制台日志面。Debug/Release 外置测试各 163/163；Public API 3.3.0 基线与项目合同通过；
  Mercury Smoke PASS（含全局快捷键）。未生成候选，未执行 `-DeployToZ`。

- 2026-08-08 本地（3.2.1 命令域与命令类收口）：锁定还原通过；Debug/Release 构建均 0 warning、0 error，
  外置测试各 161/161。命令集已移除长期灰置的治理复选框和服务状态按钮，只保留域、类、MCP、刷新；
  HistoryVulcan 内置命令统一属于 `HistoryVulcan` 域，模块 owner 强制域、显式/反射类、`core` 回退、
  `command.list domain=HistoryVulcan class=win`、跨进程元数据和控制台域回显均有自动化覆盖。
  `dotnet format --verify-no-changes`、3.2.1 精确 Unshipped API 基线、项目合同、零抑制/零热点质量门禁和
  `git diff --check` 通过；未生成 `b-Publish/current` 候选，未更新 `z-HistoryVulcan`。

- 2026-08-10 本地（3.3.2 诊断指令收口与测试套件重整 / DEC-024）：
  Debug/Release 各 0 warning、0 error，测试各 **161/161**；format、项目合同、质量门禁与
  公开 API 基线全部通过。
  - **执行效率**：全量测试 42 秒 → **约 25 秒**（用例数由 159 增至 161）。
    根因是 `PumpDispatcher` 无条件固定睡 300ms、43 个默认调用点；改为
    `UiTestHost.Pump()` 排空即返回后，`DockingContractTests` 5.9 秒 → 1.1 秒、
    `ShellChromeContractTests` 22.4 秒 → 14.4 秒。
    3 个用例因此暴露出被盲等掩盖的真实时间依赖，已改用具名等待：
    控制台 100ms 批量合并用 `PumpUntil`，`DockingHost` 200ms 缩放去抖用 `PumpFor(250)`。
  - **功能内聚**：`RunSta` / `PumpDispatcher` 的三份拷贝收敛为唯一所有者 `UiTestHost`；
    测试集合按争用资源分为 `ui-foreground` 与 `network-gateway`，取代程序集级
    `DisableTestParallelization` 一刀切。**如实记录：分组本身未带来可测量的墙钟收益**
    （VSTest 下两个重量级集合仍基本串行），本轮增益几乎全部来自 pump 修复；
    保留分组是因为它记录了真实的资源约束，且移除了不必要的全局串行开关。
  - **挂死可见性**：`UiTestHost.RunSta` 由裸 `Join()` 改为 60 秒硬上限并抛出具名
    `TimeoutException`（已用临时探针验证其确实触发）；`xunit.runner.json` 开启
    `diagnosticMessages` + `longRunningTestSeconds=30`；异步的
    `ServiceHostWaitsForRestartingPredecessorToReleaseMutex` 加 `Timeout=30s`
    （xUnit 的 `Timeout` 只对 async 用例生效，同步的端口用例改由长任务诊断兜底）。
  - **诊断指令收口**：`vulcan.log.flood` 默认不注册（需 `diagnostics.commands=true`）、
    标记 `Dangerous`、由 `McpExposurePolicy` 按名硬排除。
    **本轮发现的自造回归**：3.3.2 把 `debug.logflood` 收编为 `vulcan.log.flood` 时，
    原先按 `"debug."` 前缀生效的 MCP 硬排除失配，使承压注水一度可被 MCP/Web 远程触发
    （`rate=100000 × seconds=600`）。已修复并由
    `DiagnosticFloodCommandStaysOutOfReachOfRemoteClients` 加锁。
  - **环境噪声**：一次 Release 构建因残留 `testhost` 占用 `obj/Release/.../ref/HistoryVulcan.Tests.dll`
    报 MSB3883；`dotnet build-server shutdown` 后重建通过。非代码问题，如实记录。

- 2026-08-10 本地（3.3.2 正式部署到 `z-HistoryVulcan`）：`Publish-HistoryVulcanHost.ps1 -Version 3.3.2`
  生成候选（43 文件、`HistoryVulcan.exe` 3.3.2.0、5 份消费文档），审查后经 `-DeployToZ` 一次性部署；
  Z 快照 manifest 为 product=HistoryVulcan、version=3.3.2，41 个受校验文件 SHA-256 全部匹配、
  候选与 Z 逐文件差异为 0（`installer/` 按约定不计入快照校验）。
  **首次 `-DeployToZ` 失败**：`Move-Item` 报拒绝访问，原因是 Z 快照里的 3.3.1 宿主正在运行
  （前端 + 后台两个进程）占用目录。该运行实例同时解释了本轮两处测试异常——它持有 ServiceHost
  全局 mutex 与 MCP/Web 端口，导致基线测试整轮挂死 25 分钟，以及 `McpGatewayPortTests` 偶发失败。
  用户关闭前端后后台服务按设计仍在运行，停止后台进程后重跑部署成功。
  **`sourceDirty=true`**：本轮改动尚未提交，快照 `sourceCommit` 仍指向上一提交 `037a1ef7`；
  与 3.2.0 发布时的 `sourceDirty=false` 不同，提交后应重新生成快照以固定来源。
  未生成或发布 NuGet 包，未执行 Git commit、tag 或 push。

- 2026-08-10 本地（3.3.2 九类对齐、模块域去前缀、门禁解耦、旧债退役 / DEC-023）：
  Debug 与 Release 构建均 0 warning、0 error；外置测试各 **159/159**（各 42–43 秒）；
  `dotnet format --verify-no-changes`、项目合同（instantiation）、质量门禁（抑制标记 0、热点 0）
  与公开 API 基线全部通过。
  - 83 条指令完成 32 条改名，13 类 + 5 无类 + 1 影子域收敛为 9 类；新增
    `CommandTaxonomyContractTests`（15 个用例）断言三段式、九类白名单、模块域归一化与退役类缺席。
  - 拆除 `HistoryVulcan.Tests` 对 `../2026-021-HistoryMercury` 的跨仓库 `ProjectReference`
    及全部 `extern alias mercury`：删除 `CommandCatalogSessionTests`（5 用例）、
    `ConsoleCompletionTests`（9 用例）与 7 个依赖 Mercury 视图的 Shell 用例，交回 Mercury 仓库；
    `DockingContractTests` / `ShellChromeContractTests` 的中央页断言改用本地替身描述符。
    根解决方案与 CI 门禁自此只验 Vulcan 自身。
  - **拆解过程中发现并修复的既有缺陷**：控制台的 `vulcan.log.source` / `vulcan.log.class`
    此前完全委托给 Mercury 的目录会话，无 Mercury 的宿主上这两条自有指令永远失败
    （`DeferredCommandCatalogSession` 对任何域都返回 false）。已让延迟会话在未挂接真实会话时
    回退到本地 `CommandRegistry` 解析域/类，并保持「域为全部时类必须为全部」的严格两级语义。
    该缺陷此前被"测试总是带 Mercury 运行"掩盖。
  - **公开 API 门禁此前长期失效**：`Assert-PublicApiBaseline.ps1` 把基线目录硬编码为
    `public-api-baselines\3.3.0`，自 3.3.1 起每个版本都静默回退到脚本内嵌的 3.2.x 列表，
    门禁只能红不能过。已改为按 `VulcanVersion` 解析基线目录，并建立 3.3.2 基线
    （Core 95 / Services 59 / Shell 12 / ServiceHost 0 条）。
  - `vulcan.log.export` 省略 `path` 时不再弹 `SaveFileDialog`；新增
    `ConsoleExportWithoutPathWritesDefaultFileWithoutDialog` 在无人值守下断言默认文件落盘并回报绝对路径。
  - 退役：根 `eng/`（2 个无引用脚本）、`Unused/`（27 个模板 CAD 文件）、本地 `artifacts/`（56MB 旧名产物）、
    `Publish-AppShell.ps1`、`b-Publish/history/0.5.0` 与 `0.7.2`、`z-HistoryVulcan/installer.7z` 游离副本、
    `ShellWindow` 的 `Ctrl+反引号` 本地 KeyBinding；`App.TryCreateGlobalShortcutHost` 由硬编码
    `HistoryMercury.dll` / `Mercury.Input.GlobalShortcutService` 改为按 `IGlobalShortcutHost` 合同发现。
  - **偶发失败（如实记录）**：本轮首次全量 Debug 运行中
    `McpGatewayPortTests.ExplicitPortRetryPersistsActualPortAndStopClearsRuntimePort` 失败一次，
    定向复跑 4/4、完整复跑 159/159 通过，判定为端口占用导致的环境偶发，首次失败不因复跑通过而抹去。
    另外本轮开始前的一次基线测试出现整轮挂死约 25 分钟（testhost 累计 CPU 仅 9.5 秒，纯阻塞），
    杀进程后相同命令 64 秒通过。**套件缺少用例级超时，挂死时表现为静默无输出**，
    已登记为待办：应为跨进程 mutex/端口类用例加 `[Fact(Timeout=…)]` 或 runsettings 级超时。

- 2026-08-08 本地（3.2.2 源码收口）：严格域/类层级、`log.class`、Z 级显式 manifest 发现、相对路径/缺字段/重复模块
  诊断和内建 `CommandSurfaceFeature` 完成；HistoryJanus 3.1.2、HistoryMinerva 4.2.1 及 Studio 三模块完成 Vulcan
  引用迁移。Vulcan Debug 全量 165/165、0 warning、0 error；Release、格式、API、项目合同和五模块候选验证待本轮后续执行。

- 2026-08-08 本地（3.2.0 正式发布到 `z-HistoryVulcan`）：正式快照目录由计划的 `z-Package-HistoryVulcan`
  定为 `z-HistoryVulcan`，发布脚本、遗产脚本、manifest releaseRoots/排除项、质量门禁排除和全部现行/
  消费文档同步对齐。旧 3.1.9 候选先整体归档到 `b-Publish/history/3.1.9/`（入库）；脚本重建 3.2.0
  候选（`HistoryVulcan.exe` 3.2.0.0、5 份消费文档、manifest 与 SHA256SUMS），审查通过后经 `-DeployToZ`
  一次性部署到 `z-HistoryVulcan/`；快照 manifest 为 product=HistoryVulcan、version=3.2.0、
  sourceCommit=`c70706d1`、sourceDirty=false，候选与 Z 共 43 个文件 SHA-256 差异为 0。
  旧名 `z-Package-AppShell/` 3.1.9 快照在验证通过后退役删除。未生成或发布 NuGet 包，未执行 Git 推送。
- 2026-08-08 本地（3.2.0 产品改名 AppShell → HistoryVulcan）：命名空间、程序集、包 ID、宿主 EXE、
  版本属性（`VulcanVersion`）、解决方案、源码目录与 CI 工作流统一改名为 HistoryVulcan；
  package 消费文档改前缀为 `HistoryVulcan_` 并保留 `3.0_` 系列号；`Publish-AppShell.ps1` 保留为历史
  包验证/回滚入口，其当前源码打包与候选审查路径改用 HistoryVulcan 包 ID 与文件名；当前 Z 快照保持
  3.1.9 旧名 `z-Package-AppShell/`。锁定还原通过；清理全部 bin/obj 后 Release 干净重建产物
  `HistoryVulcan.exe` 3.2.0.0 且无旧名残留，Debug/Release 构建均 0 warning、0 error，外置测试各
  159/159；格式、四包公开 API、项目合同、质量门禁和 `git diff --check` 通过。首次干净 Release 构建
  因机器无法访问 nuget.org 漏洞审计端点报 NU1900，仅在重试进程内 `-p:NuGetAudit=false` 后通过，
  未修改仓库或机器配置。质量门禁保留既有 `ModuleHost.cs` 1013 行热点提示但未失败；本轮未生成候选、
  未更新 Z 快照、未推送到远端。
- 2026-08-08 本地（3.1.10 命令目录高内聚重构）：控制台候选、中央命令集和指令详情改为共享内部
  `CommandCatalogSession`，统一经 `command.list` / `command.domains` 读取目录，并通过 `command.show` 延迟缓存详情；
  前端本地注册表不存在的后台模块命令仍可检索并补全参数名和允许值。Debug/Release 构建均为 0 warning、0 error，
  外置测试各 159/159；格式、四包公开 API、项目合同、质量门禁和 `git diff --check` 通过。首次在线还原受机器中
  失效的 `127.0.0.1:7890` 代理阻断，仅在重试进程内清除代理变量后，强制更新锁文件和锁定还原均通过。
  质量门禁保留既有 `ModuleHost.cs` 1013 行热点提示但未失败；本轮未生成候选、未更新 Z 快照、未提交或推送。
- 2026-08-08 本地（3.1.9 命令集控制台检索收口）：命令集删除独立搜索框，普通布局由控制台输入实时过滤命令名、
  说明和示例；`Shift+W/S` 循环选择列表，`Tab` 回填命令名但不执行，聚焦控制台继续独占候选 Popup。锁定还原通过；
  Debug/Release 构建均为 0 warning、0 error，外置测试各 156/156；格式、公开 API、项目合同、质量门禁和
  `git diff --check` 通过。首次 Debug 全量运行中，既有前台激活合同因 Windows 测试进程不拥有全局前台权出现
  `Window.IsActive`/键盘焦点时序失败；单测独立通过后，将自动合同收敛为窗口可见、控制台键盘或逻辑焦点、
  Topmost 复位及重复唤醒不改变布局，跨程序上浮继续由 Windows 人工冒烟验证。未生成宿主候选或更新 Z 快照。
- 2026-08-07 本地（3.1.9 模块管理页修复）：模块管理页只读取 `module.list`，不再因 `command.list` 与模块摘要的
  命令数量短暂不一致而清空已加载模块；页面移除模块指令明细，并将刷新与全部重载合并为单一“刷新模块”动作，
  依次执行 `module.reload` 和 `module.list`。锁定还原通过；Debug/Release 构建均为 0 warning、0 error，外置测试
  各 156/156；格式、公开 API、项目合同、质量门禁和 `git diff --check` 通过。未生成宿主候选，未更新 Z 快照，
  未执行提交、标签或推送。首次连续 Release 全量运行时既有
  `FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole` 出现一次激活时序失败；停止人工 Z 宿主后
  单测复跑及完整 Release 复跑均通过，未发现与本轮模块页改动相关的回归。
- 2026-08-07 本地（3.1.9 版本与模块公共合同接纳）：锁定还原通过；Debug/Release 构建均为 0 warning、
  0 error，外置测试各 154/154（含模块上下文注入、禁用隔离和生命周期命令过滤）；格式、公开 API、项目合同、
  质量门禁和 `git diff --check` 通过。
  Core 8 条、Services 1 条、Shell 2 条模块宿主签名已从 Unshipped 提升到 3.1.9 Shipped 基线，四份
  Unshipped 文件恢复为空。当前源码和消费合同标记 3.1.9 为未发布候选、3.1.8 为不受支持的内部过渡版本，
  稳定消费者继续固定 3.1.7。未生成宿主候选，未执行正式部署、提交、标签或推送。
- 2026-08-07 本地（3.1.8 W/S 候选与聚焦边界修补）：Debug/Release 构建均为 0 warning、0 error，外置测试
  各 152/152，`ConsoleCompletionTests` 9/9；新增合同确认只有控制台聚焦态显示候选，`Shift+W/S` 上下循环，
  普通布局首次输入不得切换中央命令集，控制台输入和键盘焦点保持不变。
  格式、项目合同、质量门禁和 `git diff --check` 通过。首次 Release 构建因人工验收的源码 Release 前后台进程
  锁定 DLL 而失败，精确停止这两个进程后复验通过。公开 API 门禁仍被工作树中并行存在的
  模块宿主改动新增的 11 条 Unshipped API 阻断（Core 8、Services 1、Shell 2）；本轮候选修补未新增公开 API，
  也未改写该并行改动。
  未执行候选打包、正式发布或 Z 快照更新；125%/150% DPI 人工 GUI 冒烟仍待执行。
- 2026-08-07 本地（3.1.8 “轻松指令”控制台）：锁定还原通过；Debug/Release 构建均为 0 warning、0 error，
  外置测试最终各 150/150，新增控制台候选定向测试 8/8；格式、四包公开 API、项目合同、质量门禁和
  `git diff --check` 均通过，抑制标记和超过 1000 行的生产热点文件均为 0。首次全量验证时，已有
  `FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole` 因人工启动的 HistoryVulcan 前后台进程争夺
  前台激活而出现一次时序失败；关闭该人工进程后，Debug/Release 全量复验均为 150/150。自动 WPF 合同已覆盖
  Popup 上方定位、输入焦点、窄宽度、浅色/深色令牌、`Shift+Tab`/`Tab` 和注册表动态刷新；125%/150% DPI
  人工 GUI 冒烟未执行。未执行候选打包、正式发布、Z 快照更新、提交或推送。
- 2026-08-07 本地（现行文档与目录合同治理）：目录规则已合并到 `b-Office/文档中心.md`，独立
  `current/目录规范.md` 已删除；manifest 与 `AGENTS.md` 的入口同步更新，活动范围内无失效引用。
  六份 current 文档链接、manifest 文档路径、`Test-ProjectContract.ps1 -Instantiation`、四包公开 API 门禁和
  `git diff --check` 均通过。本轮未修改产品源码，未重跑 WPF 测试，未执行候选打包、正式发布或 Z 快照更新。
- 2026-08-07 本地（3.1.3 最终源码）：提交 `be49828a` 建立版本基线，提交 `6b56f529` 收口顶栏、
  浮窗主题与双 `/` 前台聚焦。Debug/Release 构建均 0 warning、0 error，定向测试 6/6、外置测试各
  135/135，格式、四包公开 API 和 `git diff --check` 通过；真实 Windows 快捷键人工审查确认前台上浮与
  直接输入正常。未执行候选打包、正式发布或 Z 快照更新；跨显示器、125%/150% DPI 和多浮窗仍需 GUI 回归。
- 2026-08-07 本地（3.1.4 控制台域与换行修复）：锁定还原在单次验证进程清除失效
  `HTTP_PROXY/HTTPS_PROXY=127.0.0.1:7890` 后通过；Debug/Release 构建均 0 warning、0 error，外置测试各
  142/142，覆盖控制台/命令集从 `command.domains` 取得同一候选、命令注册/注销同步、有效选择保留、
  未注册日志类别归 `core`，以及文档浮窗最大化/还原经 `win.float-state` 进入命令总线；格式与四包公开 API
  门禁通过。`Publish-AppShell.ps1 -Version 3.1.4`（不带 `-Publish`）
  首次受同一失效代理阻断，清除本次进程代理后重跑通过，
  基线合并后的候选首轮 Release 测试在 `FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole` 出现一次
  前台激活时序失败（141/142）；同一候选产物定向复验 1/1、完整候选重跑 Debug/Release 各 142/142。四包/符号包
  版本均为 3.1.4，PackageSmoke PASS；项目合同、格式和公开 API 门禁通过。用户人工审查确认
  控制台/命令集域筛选与长文本宽度响应符合预期。未执行正式发布或 Z 快照更新。
- 2026-08-07 本地（3.1.2 测试外置与顶栏手势）：根/组件解决方案锁定还原、Debug/Release 构建均
  0 warning、0 error，外置测试各 115/115；格式、公开 API 门禁与候选包验证通过，PackageSmoke PASS。
  首次在线还原受失效本地代理 `127.0.0.1:7890` 阻断；仅在重试进程中清除代理后通过，未修改仓库或机器配置。
- 2026-08-06 本地（3.2 双进程、快捷键与 3.1.1 顶栏）：Debug/Release 构建均 0 warning、0 error，
  测试各 108/108；格式与公开 API 门禁通过。Release 烟测确认 `--service` 端点、前端连接、后台
  `app.frontend.focus-console` 转发和健康检查；Windows 双 `/`、浮窗拖动仍需人工确认。该方案现归档至 history。
- 2026-08-07 本地（3.1.2 命令目录 JSON 解码）：Release 构建 0 warning、0 error；Debug/Release 测试各
  109/109，命令/模块目录跨进程 JSON 回归通过；完整 Debug 构建因人工测试进程占用旧 DLL 未执行。
- 2026-08-06 本地（3.1.1 顶栏与拖出规则收口）：Debug/Release 构建均 0 warning、0 error，测试各 99/99；
  格式、公开 API 门禁与 3.1.1 候选发布验证通过，PackageSmoke PASS。`Test-ProjectContract.ps1 -Instantiation`
  因根目录既有 `artifacts`、`eng`、`Unused` 不符合 a/b/z 前缀而失败；未删除或改名。人工 GUI 冒烟未执行。
- 2026-07-30 本地：锁定还原通过；Debug/Release 均 0 warning、0 error、76/76；格式通过；
  API 基线正向通过，伪条目反向测试按预期失败并已恢复。
- GitHub Actions run `30469169167`：Debug/Release、格式、公开 API 与 TRX 上传全部通过，
  总耗时 3m11s。前两轮分别暴露一次 WPF 测试偶发失败和 LF/CRLF 格式差异；未删除或排除测试，
  通过失败注释和 C# CRLF 检出合同完成收口。

- 2026-08-07 本地（3.1.5 质量更新）：Debug/Release 构建 0 warning、0 error；外置测试各 142/142；格式、公开 API、项目合同和抑制扫描通过。PackageSmoke 未单独运行，因候选包尚未生成，未触碰 b-Publish 或 z 快照。质量门禁确认抑制标记 0、超过 1000 行的生产热点文件 0。
- 3.1.5 Release 首轮在 FrontendFocusConsoleRaisesTheWindowAndRefocusesExistingConsole 出现一次已知前台激活时序失败（141/142）；定向复验 1/1、完整复验 142/142 通过，首次失败证据不作为最终通过被删除。
- 2026-08-07 本地（3.1.6 AvalonDock 瞬态异常与宿主部署）：锁定还原通过；Debug/Release 构建均
  0 warning、0 error，外置测试各 142/142；格式、公开 API、项目合同和质量门禁通过。首次并行启动
  Debug 构建与测试时，`testhost` 占用测试输出 DLL 导致构建失败；改为构建完成后再运行测试即通过。
  旧 3.1.2 候选已整体归档到 `b-Publish/history/3.1.2`；3.1.6 宿主候选与正式 Z 宿主各 35 个文件，
  SHA-256 差异为 0，`HistoryVulcan.exe` 文件版本为 3.1.6.0。未生成 NuGet 包，未执行 Git 提交、标签或推送。
- 2026-08-07 本地（3.1.7 UI 风格合同与完整宿主快照）：锁定还原通过；Debug/Release 构建均 0 warning、
  0 error，外置测试各 142/142；项目合同确认两份主题令牌的 Brush/Radius/Font/Space/Size 全部出现在
  `HistoryVulcan_UI风格与嵌入页面规范.md`；格式、公开 API、质量门禁和 `git diff --check` 通过。候选包含宿主、
  5 份消费文档、复用入口、README、manifest 和 SHA-256，共 43 个文件；与 `z-Package-AppShell` 差异为 0，
  manifest sourceCommit 为 `78efd54a`、sourceDirty=false，`HistoryVulcan.exe` 文件版本为 3.1.7.0。未生成 NuGet 包。
## 3.1.8 GUI 探针记录（2026-08-07）

- 已确认普通布局中，控制台输入首字后中央工作区不切换，输入框保持文本与键盘焦点，并显示同源 Popup 候选。
- 已确认控制台聚焦态视觉树存在 `CompletionList`，列表位于输入框上方，键盘焦点仍为 `Input`；候选列表由命令注册表快照生成。
- W/S 候选循环、无修饰 `Tab` 确认和 `Enter` 执行已由 `ConsoleCompletionTests` 覆盖并通过；本轮 Windows 自动化探针未能稳定完成逐键人工确认，不能替代人工验收。
- 探针期间前端曾因单实例启动/服务重连竞态退出；未修改用户设置、发布候选或正式 Z 快照。公共 API 门禁仍受并行模块宿主改动的 11 条 Unshipped 项阻塞，候选功能本身未新增公开 API。
- 清理人工实例后重新执行 Debug/Release 外置测试均为 152/152；Debug/Release 构建均为 0 warning、0 error，格式门禁、项目合同和质量门禁通过。
