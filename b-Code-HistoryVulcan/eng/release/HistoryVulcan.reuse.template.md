# HistoryVulcan 复用说明

这是其他项目和 AI 接入 HistoryVulcan 时应优先读取的首要入口。当前正式版本为 **{{VERSION}}**，
可运行宿主位于同级 `host/`，当前版本消费合同位于同级 `docs/`；不要扫描发布归档、历史版本或框架源码来推断用法。

## 平台与运行

- 目标框架：.NET 8。
- 正式宿主仅支持 Windows x64，并依赖已安装的 .NET 8 Desktop Runtime。
- 运行入口为 `host/HistoryVulcan.exe`；同一 EXE 以普通参数启动前端，以 `--service` 启动后台服务。
- 外置模块必须遵守同版本 API、命令和 UI 风格合同；禁止复制 HistoryVulcan 源码或修改部署目录中的程序集。

当前正式部署不提供 NuGet feed。确需把 HistoryVulcan 作为库嵌入其他宿主时，四个框架包必须从单独批准的
兼容包源取得并固定为同一版本；不要把历史 Z feed 当作当前 {{VERSION}} 合同。

兼容桌面应用只直接引用 Shell；`Core` 和 `Services` 会传递进入：

```xml
<PackageReference Include="OneHistory.HistoryVulcan.Shell" Version="{{VERSION}}" />
```

兼容无窗服务宿主只直接引用 ServiceHost；`Core` 和 `Services` 会传递进入：

```xml
<PackageReference Include="OneHistory.HistoryVulcan.ServiceHost" Version="{{VERSION}}" />
```

只有同一进程同时承担桌面 Shell 和服务宿主职责时，才同时直接引用两者。

## 命令总线如何消费（摘要）

1. **执行**：只经 `CommandBus.ExecuteAsync(text, source)` / `Validate`；不要绕过总线。
2. **发现**：`vulcan.command.list` / `show` / `domains` 是权威目录；行含域、类、方法。
3. **命名**：内置 `vulcan.<类>.<方法>`（Domain=`vulcan`，无连字符、无别名）；模块命令属模块域。
4. **工作台**：命令集/详情/补全与双 `/`（→ `mercury.wakeconsole` 组合 Vulcan 窗口指令）由 HistoryMercury 提供。
5. **详述**：见同版本 [API 与指令手册 · 命令总线如何消费](docs/HistoryVulcan_API与指令手册.md)。

## 包职责

| 包 | 职责 |
| --- | --- |
| `OneHistory.HistoryVulcan.Core` | 应用身份、命令总线、停靠/窗口、模块、MCP、日志与存储契约 |
| `OneHistory.HistoryVulcan.Services` | 设置、日志、文件状态、模块宿主、MCP/Web 服务实现 |
| `OneHistory.HistoryVulcan.Shell` | WPF 主壳、停靠界面、标准工具窗口和内置命令 |
| `OneHistory.HistoryVulcan.ServiceHost` | 无窗 WPF 服务生命周期、确认通道、自动启动和 `vulcan.svc.*` 命令 |

## 主要入口

- 桌面装配：`HistoryVulcan.Shell.ShellConfig`、`HistoryVulcan.Shell.ShellWindow`。
- 应用身份：`HistoryVulcan.Core.AppIdentity`、`HistoryVulcan.Core.ApplicationIdentity`。
- 命令系统：`CommandRegistry`、`CommandDescriptor`、`CommandBus`、`CommandResult`。
- 窗口注册：`ToolWindowDescriptor`、`IDockingService`、`IShellUiRegistrar`。
- 模块：`IUiModule`、`IModuleContext.RegisterCommands`、`ModuleHost`；正式宿主从 HistoryVesta 项目 `z-*` manifest 发现模块。
- 服务宿主：`ServiceComposition`、`ServiceHost.Run`。
- MCP：`McpGateway`；只会暴露显式允许 MCP 执行的命令，危险命令必须经过确认。

## 默认行为与约束

- `ShellConfig.EnableModules`、`EnableUiModules`、`EnableMcp` 和 `EnableRemoteManagementViews` 默认关闭；
  消费方只显式启用实际需要的能力。本地 `vulcan.command.*` 始终可用；命令集/详情与双 `/` 依赖 HistoryMercury 4.1.0。
- 设置、布局、日志、模块和 MCP 治理数据写入当前应用身份对应的数据目录，不要硬编码其他产品目录。
- 窗口使用 `ToolWindowDescriptor` 注册；不要恢复已删除的旧工作页/文档窗口接口。
- HistoryVulcan 的 `ModulesView` 是唯一模块管理页面；Janus 等消费方只配置 `StandardWindowIds.Modules` 的位置，
  不要复制第二套模块管理 UI。正式宿主使用 Z manifest 发现，不扫描 AppData Modules。
- 资源浏览与文件操作不属于 HistoryVulcan；`IWorkspaceService`、`ResourceView` 和 `res.*` 已删除，未来由独立模块提供。
- 控制台检索与操作使用 `vulcan.log.level/source/class/keyword/mute/autoscroll/clear/export/copy/focus`；无 `cls`。
- 3.3.0 起内置命令为 `vulcan.<类>.<方法>`（Domain=`vulcan`）；全局快捷键与命令工作台由 HistoryMercury 4.1.0 拥有。
- 命令名必须唯一。远程或前端执行位置由命令元数据声明，不要在消费方复制一套命令目录。
- MCP/Web 端口按应用身份派生；不要假定所有消费方共享固定端口。
- 精确签名以各 `.nupkg` 中 `lib/<TFM>/*.xml` 为准，只有遇到具体 API 问题时才读取对应 XML 文档。

## 当前版本消费合同

- [HistoryVulcan UI 风格与嵌入页面规范](docs/HistoryVulcan_UI风格与嵌入页面规范.md)
- [HistoryVulcan API 与指令手册](docs/HistoryVulcan_API与指令手册.md)
- [HistoryVulcan 模块与 MCP 接入](docs/HistoryVulcan_模块与MCP接入.md)
- [HistoryVulcan 运行时约束与已知限制](docs/HistoryVulcan_运行时约束与已知限制.md)
- [HistoryVulcan 消费变更摘要](docs/HistoryVulcan_消费变更摘要.md)

上述文档与 `host/` 中的宿主作为同一快照发布，是当前正式版本的稳定消费合同。兼容包资产仅供仍以
`PackageReference` 嵌入框架的项目使用，不是 HistoryVulcan 自身部署物。完整维护资料、测试证据、历史版本和发布归档不属于当前消费合同，需要时再从 `b-Publish/` 查阅。
