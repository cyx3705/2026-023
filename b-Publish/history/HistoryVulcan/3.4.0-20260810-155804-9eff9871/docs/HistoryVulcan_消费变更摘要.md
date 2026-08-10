# HistoryVulcan 消费变更摘要

适用版本：HistoryVulcan **3.3.2** 正式（已部署于 `z-HistoryVulcan`；在 3.3.0 三段式硬切基础上完成 DEC-023 九类对齐与模块域去品牌前缀）。

本文只记录会影响消费应用、模块作者和部署者的变化；源码施工、冻结审查、完整测试证据和发布操作不属于本文。

## 3.3.2 破坏性变更（升级必读）

**一、83 条内置指令中 32 条改名，必须逐条替换。** 类从 13 个收敛为 9 个：

| 变化 | 旧 | 新 |
|---|---|---|
| 整类并入 | `vulcan.core.*` | `vulcan.command.*`（`help` / `run` / `history`） |
| 整类并入 | `vulcan.frontend.*` | `vulcan.app.*`（`show` / `hide`；`frontend.exit` → `app.close`） |
| 改名避歧义 | `vulcan.app.exit` | `vulcan.app.quit` |
| 三类合并 | `vulcan.win.*` / `vulcan.layout.*` / `vulcan.panel.*` | `vulcan.ui.*` |
| 列表命令 | `win.list` / `layout.list` / `panel.list` | `ui.windows` / `ui.layouts` / `ui.panels` |
| 复合方法段 | `layout.save` / `panel.show` … | `ui.layoutsave` / `ui.panelshow` … |
| 无类归类 | `vulcan.listshortcuts` | `vulcan.app.shortcuts` |
| 无类归类 | `vulcan.listcorrections` / `listincidents` | `vulcan.prompt.corrections` / `prompt.incidents` |
| 无类归类 | `vulcan.proposecorrection` / `recordincident` | `vulcan.prompt.correct` / `prompt.record` |
| 影子域收回 | `debug.logflood` | `vulcan.log.flood` |

其余 51 条命令文本不变。完整逐条映射见 `../history/3.3.2-vulcan-class-realign.md`。
**不留别名**，旧名一律「未知指令」。

> 注意 `vulcan.app.show`/`hide`（前端**进程窗口**）与 `vulcan.ui.show`/`hide`（Shell **停靠窗口**）
> 是两组不同指令，仅靠类段区分。3.3.1 里前者叫 `frontend.show`、后者叫 `win.show`，
> 合并后请按类段确认调用的是哪一组。

**二、模块注册名不再与指令域对齐——域去掉 `History` 前缀。**
模块名继续叫 `HistoryJanus`，但它的指令域是 `janus`，命令写作 `janus.<类>.<方法>`。
品牌前缀留在模块名、程序集、目录和 `z-*` 快照里，不进指令域。
归一化由 `ModuleDomainNaming.ToDomain` 统一执行，`ModuleHost` 强制 owner 时调用同一函数，
模块无需也无法自行声明域。详见 API 手册 §3.3.1。

**三、命名有两种合法形态（3.4.0 修订）。** `<域>.<类>.<方法>` 是业务指令默认形态；
`<域>.<方法>` 是该域的无类直接方法。类推导纯结构判定，两段名判为无类而不再回退首段。
「无类」作为受控显示类别回到目录与命令集筛选中。

*3.3.2 曾宣布废止「无类」，3.4.0 已撤销该项（DEC-025）。* 需要为某个域插一条快捷方法时，
直接注册两段名即可，不必为一两条命令硬造一个类。

**五、MCP 网关已接线，但默认不监听。** 3.4.0 之前 `EnableMcp` 在桌面宿主里从未赋值，
网关从不构造，`vulcan.mcp.*` 全部是未知指令——MCP 实际不可用。现在宿主装配网关，
`vulcan.mcp.*` 始终可用；**装配不等于监听**，端口只在显式执行 `vulcan.mcp.start` 时打开。
持久自启动用新指令 `vulcan.mcp.autostart enabled=true` 开启，缺省为关。
自建宿主若依赖旧的"设了 EnableMcp 就自动监听"行为，需要改为显式调用其一。

**四、域聚焦。** 控制台可聚焦到某个域，之后只需输入 `类.方法`。
聚焦期间输入首段若命中**已注册域**则按绝对名解析，因此 `vulcan.*`、`mercury.go`
在任何聚焦状态下都能直接输入。**你的模块不需要提供任何「退出聚焦」指令。**
域清单取自注册表，新模块装载后自动参与解析，无需宿主改代码。

**四、诊断指令退出正式命令集。** 承压注水 `vulcan.log.flood`（3.3.1 的 `debug.logflood`）
默认不再注册，需宿主显式设置 `diagnostics.commands=true`；注册后也标记为危险指令并被
MCP/Web 硬排除。命令集里不应再看到 `debug` 类。

**五、`vulcan.log.export` 省略 `path` 时不再弹保存对话框**，改为写入应用数据目录
`exports/console-<时间戳>.txt` 并返回绝对路径。经 MCP、Web 或前端转发调用不再阻塞等待人工点击。

## 部署与引用方式

- HistoryVulcan 自身以 `host/HistoryVulcan.exe` 部署；正式副本位于 `z-HistoryVulcan`，不生成 NuGet 包。

- 3.3.0（DEC-022）内置命令一次硬切为 `vulcan.<类>.<方法>`（全小写、无连字符、不留别名；旧别名 `cls` 已删除），Domain=`vulcan`；
  命令集表格列为域|类|方法。全局快捷键（含 `GlobalShortcutService`）与命令工作台（目录会话、补全、命令集/详情）迁至 HistoryMercury 4.1.0；
  Shell 保留控制台日志面，Mercury 未挂接前仅为 `DeferredCommandCatalogSession`。无 Mercury 时双 `/` 与命令集/详情不可用；
  本地 `vulcan.command.*` 仍可用，`vulcan.app.shortcuts`（3.3.1 为 `vulcan.listshortcuts`）可能为空。
  旧→新映射见 `../history/3.3.0-vulcan-command-rename.md`。
- 3.2.2 的域和类是严格两级筛选：选择具体域后类列表只来自该域，域为“全部”时类固定为“全部”且禁用；控制台新增
  `vulcan.log.class`，命令集和控制台共享同一目录会话合同（3.3.0 起由 Mercury 实现并挂接，见上）。
- 3.2.2 正式宿主只从 HistoryVesta 项目 `<project>/z-*` 中的 `module.manifest.json` 发现 `type=HistoryVulcan.Module`
  模块，旧 AppData Modules 和曾用 `module.dir` 不再参与正式装载。模块名是命令域 owner，功能分支通过显式类声明。

- 3.2.1 将 HistoryVulcan 内置命令统一归入单一宿主域，以 `CommandClass` 区分功能分支；
  模块稳定名称就是模块域，旧模块未声明类时归入 `core`。3.3.0 起域短拼为 `vulcan` 且命令文本硬切。
- 3.1.8 是不受支持的内部过渡版本，消费方不得将其作为稳定升级目标。
- 兼容嵌入式消费应用若使用单独批准的 NuGet 源，必须固定引用同一版本的 HistoryVulcan 四包。
- 桌面应用通常引用 `OneHistory.HistoryVulcan.Shell`；无窗口服务宿主引用 `OneHistory.HistoryVulcan.ServiceHost`。
- 不再跨项目 `ProjectReference` 到 HistoryVulcan，不复制框架源码，也不修改解压后的包。
- 公开 API 签名以包内 XML 文档为准；源码仓中的四份 `PublicAPI.Shipped.txt` 只承担冻结门禁，不随运行包发布；实际命令集合以运行时注册表为准。

## 主要变化

- 3.3.0（DEC-022）内置命令硬切为 `vulcan.<类>.<方法>`，Domain=`vulcan`，命令集列为域|类|方法；
  全局快捷键与命令工作台外置到 HistoryMercury 4.1.0（详见上文部署节与映射文件）。
- 3.2.0 将产品从 AppShell 改名为 HistoryVulcan：包 ID 改为 `OneHistory.HistoryVulcan.*`，命名空间改为
  `HistoryVulcan.*`，宿主可执行文件为 `HistoryVulcan.exe`。公开 API 形状与命令语义不变；消费方升级时
  需要把包引用和 `using` 命名空间整体换为新名。正式快照入口随之迁移到 `z-HistoryVulcan`。
- 3.1.10 不改变公开 API 或命令语义；控制台候选、中央命令集和指令详情改为共享目录会话，
  统一从曾用 `command.list` / `command.domains` → 现用 `vulcan.command.list` / `vulcan.command.domains` 取快照，
  并按需通过曾用 `command.show` → 现用 `vulcan.command.show` 缓存参数详情。双进程前端因此也能
  对后台模块命令提供一致的检索、参数名和允许值补全。两个 WPF 页面仍各自负责输入/日志和列表/筛选，不合并 UI。
  3.3.0 起目录会话、补全与命令集/详情由 HistoryMercury 4.1.0 拥有；Shell 不再以内部 `CommandCatalogSession` 作为当前架构，
  仅保留控制台日志面与挂接前的 `DeferredCommandCatalogSession`。
- 3.1.9 将模块宿主上下文纳入公开合同：模块可实现 `IModuleContextAware` 获取权威命令总线、日志、设置和
  宿主数据根目录；`ModuleHost.Attach` 可接入这些宿主服务，`ShellConfig.ModuleDirectory` 可显式选择部署模块目录。
- 3.1.8 新增控制台“轻松指令”：控制台聚焦时在输入框上方显示命令、参数名和参数允许值候选；
  `Shift+W` 上移、`Shift+S` 下移，`Tab` 写入候选但不执行，`Shift+Tab` 不参与候选逻辑，`Enter` 保持唯一执行入口。
  普通布局首次非空输入通过曾用 `win.show name=mcp` → 现用 `vulcan.win.show name=mcp` 切换中央命令集，并直接按控制台文本过滤命令名、说明和示例；
  命令集删除独立搜索框，`Shift+W/S` 选择列表、`Tab` 回填命令名但不执行。候选直接读取运行期命令注册表，模块
  命令注册或注销后同步刷新；消费方无需复制补全或检索接线。3.3.0 起上述命令集/补全由 Mercury 提供，无 Mercury 时不可用。
- 3.1.7 新增 UI 风格与嵌入页面规范，浅色/深色色板、字体、字号、圆角、间距、固定尺寸、控件状态、
  顶栏归属和响应式验收随宿主快照发布；项目合同保证文档覆盖全部 Shell 视觉令牌。
- 3.1.6 过滤 AvalonDock 在窗格模板重建期间产生的特定瞬态鼠标离开异常，不再把第三方无害异常显示为
  HistoryVulcan 致命错误；其他未处理异常继续进入控制台。
- 3.1.5 统一控制台与命令集的域概念。命令集移除独立来源筛选和来源列，只按实际命令域筛选；
  注册来源继续保留在 `CommandCatalogRow.Source/SourceDetail` 等结构化合同中。控制台原“来源”选择器改为
  已注册域选择器，两页都通过曾用 `command.domains` → 现用 `vulcan.command.domains` 使用同一份运行期命令目录；未注册日志类别归入 `core`，
  不会产生控制台私有域。曾用 `log.source` → 现用 `vulcan.log.source` 保留兼容参数名语义，但改按域过滤并拒绝不存在的域。
- 3.1.5 的命令结果和进度在既有 `cmd:result` / `cmd:progress` 公开前缀后携带命令域；公开常量值不变。
  控制台禁用水平滚动，无空格长串也会按当前停靠/浮动窗格宽度软换行，拖窄和拖宽即时重新排版；
  复制和导出仍是原始逻辑文本，不插入视觉换行。
- 3.1.1 是命令管线与模块化收口版本。控制台顶部只保留级别、来源；关键字、layout 屏蔽、自动滚动、
  清屏、导出、复制和错误聚焦统一使用曾用 `log.*` → 现用 `vulcan.log.*` 命令。3.3.0 起旧别名 `cls` 已删除，清屏仅 `vulcan.log.clear`。
- 3.1.1 删除 HistoryVulcan 内置资源浏览能力：`IWorkspaceService`、`WorkspaceService`、
  `RemoteWorkspaceService`、`ShellConfig.Workspace`、`ResourceView`、`StandardWindowIds.Resource` 和
  曾用 `res.*` 均不再提供。资源浏览/文件操作请由独立模块提供；演示宿主不再生成电机页，也不注册曾用 `motor.*`。
- 业务按钮统一进入 `CommandBus`：窗口状态使用曾用 `app.window` → 现用 `vulcan.app.window`，错误跳转使用曾用 `log.focus` → 现用 `vulcan.log.focus`，命令示例复制使用
  曾用 `command.copy-example` → 现用 `vulcan.command.copyexample`，面板文件/目录选择使用
  曾用 `panel.select-file` / `panel.select-directory` → 现用 `vulcan.panel.selectfile` / `vulcan.panel.selectdirectory`。3.1.5 进一步把
  文档浮窗最大化/还原收进曾用 `win.float-state` → 现用 `vulcan.win.floatstate`，顶部回执、全局控制台快捷键和系统关闭隐藏也复用现有命令。
- HistoryVulcan 的 `ModulesView` 是唯一模块管理页面。Janus 只保留 `StandardWindowIds.Modules` 的位置与消费配置，
  不复制模块管理 UI；3.1.1 不改变现有模块目录抽象或全局 z 级扫描范围。
- 3.1.9 模块管理页只读取曾用 `module.list` → 现用 `vulcan.module.list` 展示已加载模块，不再因曾用 `command.list` → 现用 `vulcan.command.list` 计数不同而清空列表；所选模块的
  指令明细区域已移除，统一到命令集查阅；“刷新”和“重载全部模块”合并为单一“刷新模块”动作。

- 3.1.0 是**纯外观版本**：公开 API、命令、参数、布局文件格式与 3.0.3 完全一致，升级只改版本号，
  不需要改消费方代码。界面变化为：停靠页面改为无边框悬浮卡片（页面之间以间隙和投影分隔）；
  常驻菜单栏折叠进顶栏右上角的菜单按钮，位置恒在最小化/最大化/关闭三个按钮左侧；页面最大化时
  不再显示该页标题条与页签，内容铺满工作区（退出用顶栏「退出专注」按钮、`Esc`、`F11` 或菜单）；
  底部状态栏取消，指令结果改为右下角瞬时提示，错误计数与布局名移到顶栏。
  需要保留 3.0.3 外观的消费方请继续固定 3.0.3——3.1.0 不提供切回旧外观的开关。
- 3.1.0 起，宿主若把 `WindowStyle` 改成非 `SingleBorderWindow`，框架只跳过窗体非客户区接管，
  菜单与三个窗口按钮仍然存在且可用。
- 3.1.0 没有独立标题栏：菜单按钮与最小化/最大化/关闭并入最上一排页签所在的那一行，
  页签之外的空白即窗体拖动区。窗体标题仍是 `Window.Title`（任务栏与 Alt+Tab 可见）。
- 3.1.0 新增曾用 `app.theme mode=light|dark|toggle` → 现用 `vulcan.app.theme mode=light|dark|toggle` 指令与「视图」菜单入口，主题写入设置项
  `ui.theme`，下次启动沿用。主题色统一为暗黄；深色模式底色为墨绿、正文为淡黄。
- 3.1.0 起工具页不再有自己的标题栏，页面动作（▼ 浮动 / 停靠为文档 / 自动隐藏 / 隐藏，以及 ✕ 隐藏）
  占据该页页签行的右端；右键页签仍可打开完整菜单。
- 消费方若在自己的视图里使用 `ListBox`/`ListView`/`TabControl`，请勿覆盖 Shell 提供的模板：
  WPF 默认模板会在禁用态与内容区刷上系统浅灰（`#F4F4F4`），深色模式下会漏色。
- 浮动窗口不显示独立系统标题栏；唯一页面顶栏跟随主题，并由 HistoryVulcan 统一处理延迟拖动、双击和最大化恢复。
- 消费方若自建 `GridView` 表格，表头需显式设置
  `ColumnHeaderContainerStyle="{StaticResource Shell.GridHeader}"`（样式在
  `/HistoryVulcan.Shell;component/Themes/ShellControls.xaml`），否则 WPF 的默认表头容器样式
  不随主题变化，深色模式下会留一条浅色表头。
- 3.0.3 起，加载历史布局时会把同一侧的多个窗格合并为一个标签组；左右或上下侧栏合计最多占宿主 50%，
  中央主工作区始终至少保留 50%。
- 3.0.2 起，运行期注册或停靠到 `DockSide.Right` 的模块窗口复用现有右侧标签组，不再逐窗口创建独立侧栏；
  无右侧窗格时才新建。模块不需要为此声明 `DefaultTabTarget`。
- 3.0.1 起，`EnableModules` 和 `EnableMcp` 从默认开启改为默认关闭；`EnableUiModules` 与
  `EnableRemoteManagementViews` 继续默认关闭。消费方必须显式选择可选宿主能力。
- MCP 关闭时，本地 `vulcan.command.*` 仍保留；框架不会创建 MCP 网关、治理/审计对象或监听端口。
  中央命令集/详情依赖 HistoryMercury 4.1.0，无 Mercury 时不可用。
- 命令集进入 HistoryVulcan 的固定中央主区（由 Mercury 提供页面）；业务中央窗口使用 `DockSide.Center`。普通四边工具页可拖入中央成为
  标签页，并可再次拖回；布局保存/恢复保留嵌入位置。
- 工具窗口统一使用 `ToolWindowDescriptor`、`IDockingService` 或 `IShellUiRegistrar`，消费方不直接依赖 AvalonDock 类型。
- 模块命令、窗口和面板都由 HistoryVulcan 模块宿主发现、注册、重载和回收。
- 前端连接后由运行时发布权威命令目录，消费方不得维护第二套前端命令清单。
- MCP 和 Web 的默认端口按应用身份派生并在冲突时有限顺延，不能假定所有应用使用固定端口。
- 3.1.2 修复双进程前端的命令集页面：后台通过 JSON 返回的曾用 `command.list`、`command.show` → 现用 `vulcan.command.list`、`vulcan.command.show` 和模块清单会在 Shell
  侧还原为结构化类型，不再出现“命令集加载失败”而控制台仍能执行命令的分裂状态。

## 已移除能力

3.1.1 不再提供 `IWorkspaceService`、`WorkspaceService`、`RemoteWorkspaceService`、`ShellConfig.Workspace`、
`ResourceView`、曾用 `res.*` 或演示曾用 `motor.*`；旧数据接口 `IDataService`、`SqliteDataService`、`RemoteDataService`、
`TableView`、`ShellConfig.DataService` 和曾用 `db.*` 也不提供。需要资源或数据能力的消费应用应在自己的模块或工具窗口中实现，
并通过自己的业务命令暴露。

## 升级验收

升级后至少验证包版本一致、启动与关闭、窗口布局、命令目录、模块重载以及实际使用的 MCP/Web 能力。详细的维护、候选验证和回滚流程不属于消费合同，也不随包发布。
