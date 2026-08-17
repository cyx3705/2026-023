# AppShell 3.0 消费变更摘要

适用版本：AppShell 3.0.x。

本文只记录会影响消费应用、模块作者和部署者的变化；源码施工、冻结审查、完整测试证据和发布操作不属于本文。

## 引用方式

- 消费应用固定引用同一版本的 AppShell NuGet 包。
- 桌面应用通常引用 `OneHistory.AppShell.Shell`；无窗口服务宿主引用 `OneHistory.AppShell.ServiceHost`。
- 不再跨项目 `ProjectReference` 到 AppShell，不复制框架源码，也不修改解压后的包。
- 公开 API 签名以包内 XML 文档为准；源码仓中的四份 `PublicAPI.Shipped.txt` 只承担冻结门禁，不随运行包发布；实际命令集合以运行时注册表为准。

## 主要变化

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

## 已移除能力

3.0.x 不再提供 `IDataService`、`SqliteDataService`、`RemoteDataService`、`TableView`、`ShellConfig.DataService` 或 `db.*`。需要数据能力的消费应用应在自己的模块或工具窗口中实现，并通过自己的业务命令暴露。

## 升级验收

升级后至少验证包版本一致、启动与关闭、窗口布局、命令目录、模块重载以及实际使用的 MCP/Web 能力。详细的维护、候选验证和回滚流程不属于消费合同，也不随包发布。
