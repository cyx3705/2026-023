# AppShell 消费变更摘要

适用版本：AppShell 3.1.9 候选（包含 3.0.3 冻结基线、3.1.1 命令收口、3.1.2 命令目录修复、3.1.3 浮窗几何收口、3.1.5 质量更新、3.1.6 异常修复、3.1.7 UI 风格合同、3.1.8 轻松指令过渡实现和 3.1.9 模块宿主合同）。

本文只记录会影响消费应用、模块作者和部署者的变化；源码施工、冻结审查、完整测试证据和发布操作不属于本文。

## 部署与引用方式

- AppShell 自身以 `host/AppShell.exe` 部署；当前稳定消费快照为 3.1.7，3.1.9 尚未部署且不生成 NuGet 包。
- 3.1.8 是不受支持的内部过渡版本，消费方不得将其作为稳定升级目标。
- 兼容嵌入式消费应用若使用单独批准的 NuGet 源，必须固定引用同一版本的 AppShell 四包。
- 桌面应用通常引用 `OneHistory.AppShell.Shell`；无窗口服务宿主引用 `OneHistory.AppShell.ServiceHost`。
- 不再跨项目 `ProjectReference` 到 AppShell，不复制框架源码，也不修改解压后的包。
- 公开 API 签名以包内 XML 文档为准；源码仓中的四份 `PublicAPI.Shipped.txt` 只承担冻结门禁，不随运行包发布；实际命令集合以运行时注册表为准。

## 主要变化

- 3.1.9 将模块宿主上下文纳入公开合同：模块可实现 `IModuleContextAware` 获取权威命令总线、日志、设置和
  宿主数据根目录；`ModuleHost.Attach` 可接入这些宿主服务，`ShellConfig.ModuleDirectory` 可显式选择部署模块目录。
- 3.1.8 新增控制台“轻松指令”：控制台聚焦时在输入框上方显示命令、参数名和参数允许值候选；
  `Shift+W` 上移、`Shift+S` 下移，`Tab` 写入候选但不执行，`Shift+Tab` 不参与候选逻辑，`Enter` 保持唯一执行入口。
  普通布局首次非空输入通过 `win.show name=mcp` 切换中央命令集，并直接按控制台文本过滤命令名、说明和示例；
  命令集删除独立搜索框，`Shift+W/S` 选择列表、`Tab` 回填命令名但不执行。候选直接读取运行期命令注册表，模块
  命令注册或注销后同步刷新；消费方无需复制补全或检索接线。
- 3.1.7 新增 UI 风格与嵌入页面规范，浅色/深色色板、字体、字号、圆角、间距、固定尺寸、控件状态、
  顶栏归属和响应式验收随宿主快照发布；项目合同保证文档覆盖全部 Shell 视觉令牌。
- 3.1.6 过滤 AvalonDock 在窗格模板重建期间产生的特定瞬态鼠标离开异常，不再把第三方无害异常显示为
  AppShell 致命错误；其他未处理异常继续进入控制台。
- 3.1.5 统一控制台与命令集的域概念。命令集移除独立来源筛选和来源列，只按实际命令域筛选；
  注册来源继续保留在 `CommandCatalogRow.Source/SourceDetail` 等结构化合同中。控制台原“来源”选择器改为
  已注册域选择器，两页都通过 `command.domains` 使用同一份运行期命令目录；未注册日志类别归入 `core`，
  不会产生控制台私有域。`log.source` 保留兼容命令名和参数名，但改按域过滤并拒绝不存在的域。
- 3.1.5 的命令结果和进度在既有 `cmd:result` / `cmd:progress` 公开前缀后携带命令域；公开常量值不变。
  控制台禁用水平滚动，无空格长串也会按当前停靠/浮动窗格宽度软换行，拖窄和拖宽即时重新排版；
  复制和导出仍是原始逻辑文本，不插入视觉换行。
- 3.1.1 是命令管线与模块化收口版本。控制台顶部只保留级别、来源；关键字、layout 屏蔽、自动滚动、
  清屏、导出、复制和错误聚焦统一使用 `log.*` 命令。`cls` 仅作 `log.clear` 兼容别名。
- 3.1.1 删除 AppShell 内置资源浏览能力：`IWorkspaceService`、`WorkspaceService`、
  `RemoteWorkspaceService`、`ShellConfig.Workspace`、`ResourceView`、`StandardWindowIds.Resource` 和
  `res.*` 均不再提供。资源浏览/文件操作请由独立模块提供；演示宿主不再生成电机页，也不注册 `motor.*`。
- 业务按钮统一进入 `CommandBus`：窗口状态使用 `app.window`，错误跳转使用 `log.focus`，命令示例复制使用
  `command.copy-example`，面板文件/目录选择使用 `panel.select-file` / `panel.select-directory`。3.1.5 进一步把
  文档浮窗最大化/还原收进 `win.float-state`，顶部回执、全局控制台快捷键和系统关闭隐藏也复用现有命令。
- AppShell 的 `ModulesView` 是唯一模块管理页面。OHS 只保留 `StandardWindowIds.Modules` 的位置与消费配置，
  不复制模块管理 UI；3.1.1 不改变现有模块目录抽象或全局 z 级扫描范围。
- 3.1.9 模块管理页只读取 `module.list` 展示已加载模块，不再因 `command.list` 计数不同而清空列表；所选模块的
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
- 3.1.0 新增 `app.theme mode=light|dark|toggle` 指令与「视图」菜单入口，主题写入设置项
  `ui.theme`，下次启动沿用。主题色统一为暗黄；深色模式底色为墨绿、正文为淡黄。
- 3.1.0 起工具页不再有自己的标题栏，页面动作（▼ 浮动 / 停靠为文档 / 自动隐藏 / 隐藏，以及 ✕ 隐藏）
  占据该页页签行的右端；右键页签仍可打开完整菜单。
- 消费方若在自己的视图里使用 `ListBox`/`ListView`/`TabControl`，请勿覆盖 Shell 提供的模板：
  WPF 默认模板会在禁用态与内容区刷上系统浅灰（`#F4F4F4`），深色模式下会漏色。
- 浮动窗口不显示独立系统标题栏；唯一页面顶栏跟随主题，并由 AppShell 统一处理延迟拖动、双击和最大化恢复。
- 消费方若自建 `GridView` 表格，表头需显式设置
  `ColumnHeaderContainerStyle="{StaticResource Shell.GridHeader}"`（样式在
  `/AppShell.Shell;component/Themes/ShellControls.xaml`），否则 WPF 的默认表头容器样式
  不随主题变化，深色模式下会留一条浅色表头。
- 3.0.3 起，加载历史布局时会把同一侧的多个窗格合并为一个标签组；左右或上下侧栏合计最多占宿主 50%，
  中央主工作区始终至少保留 50%。
- 3.0.2 起，运行期注册或停靠到 `DockSide.Right` 的模块窗口复用现有右侧标签组，不再逐窗口创建独立侧栏；
  无右侧窗格时才新建。模块不需要为此声明 `DefaultTabTarget`。
- 3.0.1 起，`EnableModules` 和 `EnableMcp` 从默认开启改为默认关闭；`EnableUiModules` 与
  `EnableRemoteManagementViews` 继续默认关闭。消费方必须显式选择可选宿主能力。
- MCP 关闭时，本地 `command.*` 和中央命令集仍保留；框架不会创建 MCP 网关、治理/审计对象或监听端口。
- 命令集进入 AppShell 的固定中央主区；业务中央窗口使用 `DockSide.Center`。普通四边工具页可拖入中央成为
  标签页，并可再次拖回；布局保存/恢复保留嵌入位置。
- 工具窗口统一使用 `ToolWindowDescriptor`、`IDockingService` 或 `IShellUiRegistrar`，消费方不直接依赖 AvalonDock 类型。
- 模块命令、窗口和面板都由 AppShell 模块宿主发现、注册、重载和回收。
- 前端连接后由运行时发布权威命令目录，消费方不得维护第二套前端命令清单。
- MCP 和 Web 的默认端口按应用身份派生并在冲突时有限顺延，不能假定所有应用使用固定端口。
- 3.1.2 修复双进程前端的命令集页面：后台通过 JSON 返回的 `command.list`、`command.show` 和模块清单会在 Shell
  侧还原为结构化类型，不再出现“命令集加载失败”而控制台仍能执行命令的分裂状态。

## 已移除能力

3.1.1 不再提供 `IWorkspaceService`、`WorkspaceService`、`RemoteWorkspaceService`、`ShellConfig.Workspace`、
`ResourceView`、`res.*` 或演示 `motor.*`；旧数据接口 `IDataService`、`SqliteDataService`、`RemoteDataService`、
`TableView`、`ShellConfig.DataService` 和 `db.*` 也不提供。需要资源或数据能力的消费应用应在自己的模块或工具窗口中实现，
并通过自己的业务命令暴露。

## 升级验收

升级后至少验证包版本一致、启动与关闭、窗口布局、命令目录、模块重载以及实际使用的 MCP/Web 能力。详细的维护、候选验证和回滚流程不属于消费合同，也不随包发布。
