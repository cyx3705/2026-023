# HistoryVulcan 运行时约束与已知限制

适用版本：HistoryVulcan **3.3.0** 正式（已部署于 `z-HistoryVulcan`）；`3.1.8` 是不受支持的内部过渡版本。
3.3.0（DEC-022）：内置命令硬切为 `vulcan.<类>.<方法>`；全局快捷键（含 `GlobalShortcutService`）与命令工作台由 HistoryMercury 4.1.0 拥有。
旧→新映射见 `../history/3.3.0-vulcan-command-rename.md`。
本文件记录消费应用必须遵守的运行时约束、默认值和已知限制。

## 顶栏与浮窗

- 主 HistoryVulcan 处于普通状态时，空白顶栏越过系统阈值即移动；主窗口最大化状态及独立浮窗仍需按住满 120ms。最大化宿主使用两倍阈值，并在下拖时恢复为普通窗口。
- 嵌入或专注页面只能拖真实页签成为独立浮窗；普通页签使用 AvalonDock 原生流程，专注页签先恢复布局再浮动。
- 新浮窗采用恢复后嵌入窗格的实际宽高，并保持鼠标在原页签抓取位置；跨显示器定位按目标显示器 DPI 与工作区裁剪。
- 这些行为属于 Shell 内部实现，不新增公共 .NET API；消费方不得依赖 AvalonDock internal API 覆盖该流程。

## 控制台与命令集

- 命令集页面只按命令名域筛选：取第一个 `.` 前缀，无点命令归 `core`；注册来源仍保留在结构化目录数据中。
- 控制台与命令集的域都来自命令总线 `vulcan.command.domains`，两页显示相同的运行期已注册域集合。
- 命令回显、结果和进度按命令域归组；普通日志类别前缀只有命中已注册域时采用，否则归入 `core`，不产生私有域。
- `vulcan.log.source` 参数候选由运行期命令目录生成；不存在的域会返回失败和当前可用域。
- 3.3.0 起全局快捷键与命令工作台（目录会话、补全、命令集/详情）由 HistoryMercury 4.1.0 拥有；
  Shell 仅保留控制台日志面，Mercury 未挂接前为 `DeferredCommandCatalogSession`。
  无 Mercury 时：双 `/` 与命令集/详情 UI 不可用；`vulcan.listshortcuts` 可能为空；本地 `vulcan.command.*` 仍可用。
- 输出没有水平滚动条；长文本只在视觉上随当前窗格宽度换行，复制和导出不插入软换行。

## 运行时边界

| 边界 | 结论 |
|---|---|
| 无界集合 | 运行时日志缓冲、文件写入队列、控制台显示与入站队列、命令历史均有上限；Web 每客户端日志广播队列固定为 256 条并在满时丢最旧，限流窗口、前端目录缓存和 MCP 会话缓存也有上限与清理点。命令注册表、设置及审计/治理记录属于显式配置或不可静默淘汰的持久资产，边界见下文。 |
| 未释放资源 | `McpGateway`、`WebGateway` 的监听器和 `CancellationTokenSource` 均在 Stop/Dispose 释放；WebSocket 会话、文件监视器、去抖定时器、日志流及模块加载上下文均有退出路径。 |
| 硬编码 | MCP/Web 默认端口按稳定应用名派生并可显式覆盖；端口重试、限流、缓存容量、命令历史、控制台容量、MCP 执行/确认超时均可配置。协议报文 1 MiB 上限和 Web 前端中继 15 秒超时是 3.0 安全契约，不作为业务调优项。 |
| 跨应用共享资源 | `%AppData%/<应用名>/`、ServiceHost 本地互斥体、MCP/Web 默认端口均按稳定应用名隔离；端口冲突自动顺延。相同 `ServiceName` 的 ServiceHost 仍为有意的单实例服务。 |
| null 降级路径 | `CommandSelection=null` 时 Shell 创建本地状态；MCP/提示词治理不依赖数据库；前端离线时前端目录仍可查阅，执行返回明确失败。无 Mercury 时命令集/详情与双 `/` 不可用，但不影响本地 `vulcan.command.*`。 |
| 线程亲和 | 窗口、布局、面板、对话框和控制台命令均声明 `RequiresUiThread`；CommandBus 统一编组到 `UiContext`。ModuleHost 只通过注入的 `SynchronizationContext` 创建/销毁 UI，无窗服务保持 null。 |

## 默认值

| Shell 能力 | 默认值 | 启用方式 |
|---|---:|---|
| 模块命令宿主 | 关闭 | `ShellConfig.EnableModules=true` |
| UI 模块宿主 | 关闭 | `ShellConfig.EnableUiModules=true`，或随 `EnableModules=true` 启用 |
| MCP 网关与治理 | 关闭 | `ShellConfig.EnableMcp=true`；只装配不监听时另设 `mcp.autostart=false` |
| 远程管理视图 | 关闭 | `ShellConfig.EnableRemoteManagementViews=true` |
| 本地 `vulcan.command.*` | 开启 | Shell 核心能力，不创建网络监听 |
| 命令集/详情与双 `/` | 依赖 Mercury | HistoryMercury 拥有命令工作台与 `GlobalShortcutService`；双 `/` → `mercury.wakeconsole` 再组合 Vulcan 窗口指令；无 Mercury 时不可用 |

| 设置键 | 默认值 | 说明 |
|---|---:|---|
| `mcp.port` | 未设置时 `8737 + stableHash(appName) % 200` | 显式值优先 |
| `mcp.portretries` | `20` | 初始端口冲突后的顺延次数，范围 `0..100` |
| `mcp.sessionlimit` | `1024` | MCP 会话缓存上限，范围 `16..65536` |
| `web.port` | 未设置时 `8938 + stableHash(appName) % 200` | 显式值优先 |
| `web.portretries` | `20` | 初始端口冲突后的顺延次数，范围 `0..100` |
| `web.ratelimit` | `120` | 失败鉴权尝试按远端地址限流，成功请求按会话限流，均按每分钟计，范围 `10..10000`；更换 `X-Session-Id` 不能绕过失败鉴权限流 |
| `web.ratewindowlimit` | `4096` | 限流窗口缓存上限，范围 `128..65536` |
| `web.frontendcataloglimit` | `32` | 离线前端目录缓存上限，范围 `1..256` |
| `console.buffer` | `50000` | 控制台行和入站事件上限，最小 `1000` |
| `console.history` | `500` | 已脱敏手输命令历史上限，最小 `10`；3.0 首次启动清空无 v2 脱敏格式头的旧历史 |

## 已知限制

1. `SettingsService` 接受应用自定义键，`PromptGovernanceStore` 保存修订、提案、纠错和事故的完整审计链，`McpAuditRecorder` 追加保存 `state/mcp-history.jsonl`。单条 MCP 审计的 Client、Tool、Arguments、Result 分别限制为 256、128、500、100 字符，客户端字段以稳定会话 ID 在前；JSONL 文件本身在 3.0 不做静默轮转或淘汰。长期自动化宿主应由运维流程在停服或完成一致性快照后归档旧文件；MCP 审计轮转与归档策略计划在 3.1 提供。
2. 同一应用名的多个桌面实例共享 `%AppData%/<应用名>/`。布局和设置采用原子覆盖但不提供跨进程事务；日志文件被另一实例占用时，该实例可能只保留内存/控制台日志。需要并行运行隔离实例时，应使用不同的稳定应用名。
3. 前端目录按 `FrontendName` 持久缓存。超过 `web.frontendcataloglimit` 时会确定性淘汰其它离线应用目录；在线会话目录不受影响，重新连接会再次发布。
4. `ShellServiceClient` 在建立 WebSocket 时发布目录。运行期间动态注册的新命令需重连后进入服务端权威目录；HistoryVulcan 的内置命令、启动期应用命令和启动期模块命令均在连接前完成注册。
5. `McpExposurePolicy` 的兼容装配委托仍是进程级静态入口。3.0 已保证并发读写安全，但不支持同一进程承载多套彼此不同的模块暴露策略；此类多租户隔离将在 3.1 通过实例策略演进。普通“一桌面 Shell + 一无窗 ServiceHost”分进程部署不受影响。
6. `CommandManualGenerator.Render` 使用调用前确定的 `AppIdentity.Current` 生成标题和版本。宿主必须在生成手册前调用 `AppIdentity.Use(...)`。

遇到上述限制时，应记录根因和复现；禁止在消费方复制命令清单、删除布局文件或改写框架状态来掩盖问题。
