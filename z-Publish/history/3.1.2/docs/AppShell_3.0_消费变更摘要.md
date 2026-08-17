# AppShell 消费变更摘要

适用版本：AppShell 3.1.2（包含 3.0.3 冻结基线、3.1.1 命令收口和本轮命令目录修复）。

本文只记录会影响消费应用、模块作者和部署者的变化；源码施工、冻结审查、完整测试证据和发布操作不属于本文。

## 引用方式

- 消费应用固定引用同一版本的 AppShell NuGet 包。
- 桌面应用通常引用 `OneHistory.AppShell.Shell`；无窗口服务宿主引用 `OneHistory.AppShell.ServiceHost`。
- 不再跨项目 `ProjectReference` 到 AppShell，不复制框架源码，也不修改解压后的包。
- 公开 API 签名以包内 XML 文档为准；源码仓中的四份 `PublicAPI.Shipped.txt` 只承担冻结门禁，不随运行包发布；实际命令集合以运行时注册表为准。

## 主要变化

- 3.1.1 是命令管线与模块化收口版本。控制台顶部只保留级别、来源；关键字、layout 屏蔽、自动滚动、
  清屏、导出、复制和错误聚焦统一使用 `log.*` 命令。`cls` 仅作 `log.clear` 兼容别名。
- 3.1.1 删除 AppShell 内置资源浏览能力：`IWorkspaceService`、`WorkspaceService`、
  `RemoteWorkspaceService`、`ShellConfig.Workspace`、`ResourceView`、`StandardWindowIds.Resource` 和
  `res.*` 均不再提供。资源浏览/文件操作请由独立模块提供；演示宿主不再生成电机页，也不注册 `motor.*`。
- 业务按钮统一进入 `CommandBus`：窗口状态使用 `app.window`，错误跳转使用 `log.focus`，命令示例复制使用
  `command.copy-example`，面板文件/目录选择使用 `panel.select-file` / `panel.select-directory`。
- AppShell 的 `ModulesView` 是唯一模块管理页面。OHS 只保留 `StandardWindowIds.Modules` 的位置与消费配置，
  不复制模块管理 UI；3.1.1 不改变现有模块目录抽象或全局 z 级扫描范围。

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
- 浮动窗口的客户区与外框跟随主题；其系统标题栏由 Windows 绘制，深色模式下不变色。
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
