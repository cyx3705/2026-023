# AppShell 复用说明

这是其他项目和 AI 接入 AppShell 时应优先读取的首要入口。当前正式版本为 **3.0.3**，正式包位于同级 `feed/`，当前版本消费合同位于同级 `docs/`；不要扫描发布归档、历史版本或框架源码来推断用法。

## 平台与引用

- 目标框架：.NET 8。
- `Shell` 和 `ServiceHost` 仅支持 Windows，并依赖 WPF。
- 包版本必须固定一致；禁止跨项目 `ProjectReference`、源码复制和直接修改 AppShell 包。
- 本地包源指向 `z-Package-AppShell/feed`，第三方依赖仍从 NuGet.org 恢复。

桌面应用只直接引用 Shell；`Core` 和 `Services` 会传递进入：

```xml
<PackageReference Include="OneHistory.AppShell.Shell" Version="3.0.3" />
```

无窗服务宿主只直接引用 ServiceHost；`Core` 和 `Services` 会传递进入：

```xml
<PackageReference Include="OneHistory.AppShell.ServiceHost" Version="3.0.3" />
```

只有同一进程同时承担桌面 Shell 和服务宿主职责时，才同时直接引用两者。

## 包职责

| 包 | 职责 |
| --- | --- |
| `OneHistory.AppShell.Core` | 应用身份、命令总线、停靠/窗口、模块、MCP、日志与存储契约 |
| `OneHistory.AppShell.Services` | 设置、日志、工作区、模块宿主、MCP/Web 服务实现 |
| `OneHistory.AppShell.Shell` | WPF 主壳、停靠界面、标准工具窗口和内置命令 |
| `OneHistory.AppShell.ServiceHost` | 无窗 WPF 服务生命周期、确认通道、自动启动和 `svc.*` 命令 |

## 主要入口

- 桌面装配：`AppShell.Shell.ShellConfig`、`AppShell.Shell.ShellWindow`。
- 应用身份：`AppShell.Core.AppIdentity`、`AppShell.Core.ApplicationIdentity`。
- 命令系统：`CommandRegistry`、`CommandDescriptor`、`CommandBus`、`CommandResult`。
- 窗口注册：`ToolWindowDescriptor`、`IDockingService`、`IShellUiRegistrar`。
- 工作区：`IWorkspaceService`、`WorkspaceService`。
- 模块：`IUiModule`、`ModuleHost`；模块 DLL 与同名 `.panel.json` 放在应用数据目录的 `Modules` 下。
- 服务宿主：`ServiceComposition`、`ServiceHost.Run`。
- MCP：`McpGateway`；只会暴露显式允许 MCP 执行的命令，危险命令必须经过确认。

## 默认行为与约束

- `ShellConfig.EnableModules`、`EnableUiModules`、`EnableMcp` 和 `EnableRemoteManagementViews` 默认关闭；
  消费方只显式启用实际需要的能力。本地 `command.*` 与命令集主窗口不依赖 MCP 网关。
- 设置、布局、日志、模块和 MCP 治理数据写入当前应用身份对应的数据目录，不要硬编码其他产品目录。
- 窗口使用 `ToolWindowDescriptor` 注册；不要恢复已删除的旧工作页/文档窗口接口。
- 命令名必须唯一。远程或前端执行位置由命令元数据声明，不要在消费方复制一套命令目录。
- MCP/Web 端口按应用身份派生；不要假定所有消费方共享固定端口。
- 精确签名以各 `.nupkg` 中 `lib/<TFM>/*.xml` 为准，只有遇到具体 API 问题时才读取对应 XML 文档。

## 当前版本消费合同

- [AppShell API 与指令手册](docs/AppShell_API与指令手册.md)
- [AppShell 3.0 模块与 MCP 接入](docs/AppShell_3.0_模块与MCP接入.md)
- [AppShell 3.0 运行时约束与已知限制](docs/AppShell_3.0_运行时约束与已知限制.md)
- [AppShell 3.0 消费变更摘要](docs/AppShell_3.0_消费变更摘要.md)

上述文档与 `feed/` 中的包作为同一快照发布，是当前正式版本的稳定消费合同。完整维护资料、测试证据、历史版本和发布归档不属于当前消费合同，需要时再从 `b-Publish/` 查阅。
