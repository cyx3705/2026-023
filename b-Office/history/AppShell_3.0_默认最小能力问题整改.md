# AppShell 3.0 默认最小能力问题整改

> 发现日期：2026-07-29
> 影响版本：3.0.0 正式包
> 修复版本：3.0.1 候选
> 状态：实现、全量门禁与消费方验证完成，源码已推送；未正式发布、未冻结

## 1. 问题

3.0.0 的 `ShellConfig.EnableMcp` 和 `EnableModules` 默认值均为 `true`。消费方只填写应用名和版本并创建
`ShellWindow` 时，框架会隐式创建 MCP 网关、提示词治理和审计对象，尝试监听本地端口，同时启动模块目录
扫描、文件监视和模块命令装载。

这违反包消费模式下的装配责任：AppShell 应提供能力，但不应替消费方选择网络服务、插件加载或后台监视。
默认构造必须是最小 Shell，所有可选运行时能力由消费方显式启用。

单纯把 `EnableMcp` 改为 `false` 还会让中央命令集与 `command.*` 一并消失，重新形成空中央区。因此本次整改
同时拆除“本地命令目录依赖 MCP 网关”的错误耦合。

## 2. 整改原则

1. 默认不装配：未设置可选开关时，不创建对应宿主、存储、审计器、监视器或网络监听。
2. 显式启用即生效：消费方设置 `EnableMcp=true` 或 `EnableModules=true` 后，保持原有完整能力。
3. 装配与监听可分离：MCP 已显式启用但设置 `mcp.autostart=false` 时，只装配命令和治理能力，不自动监听。
4. 核心 Shell 不降级：命令总线、本地 `command.*`、中央命令集、指令详情、停靠与布局仍是基础能力。
5. 正式包不可覆盖：3.0.0 归档和 Z 级当前快照保持不变，修复以 3.0.1 候选继续验证。

## 3. 同类项审计

| 项目 | 3.0.0 行为 | 结论与处理 |
|---|---|---|
| `ShellConfig.EnableMcp` | 默认 `true`，隐式创建网关并尝试监听 | 缺陷；改为默认 `false` |
| `ShellConfig.EnableModules` | 默认 `true`，隐式扫描模块并启动文件监视 | 同类缺陷；改为默认 `false` |
| `EnableUiModules` | 默认 `false` | 已符合；保持 |
| `EnableRemoteManagementViews` | 默认 `false` | 已符合；保持 |
| Workspace | 仅在消费方传入非 null 服务时装配 `res.*` | 显式依赖注入；保持 |
| 面板 | C# 清单默认为空；JSON 文件存在本身是消费方配置 | 无后台服务或网络副作用；保持 |
| `ServiceComposition` 的 Modules/Mcp/Web | 默认 null，只有消费方传入对象才启动 | 已符合；保持 |
| `RegisterAutostartOnFirstRun` | 默认 `false` | 已符合；保持 |
| `McpGateway.AutostartEnabled` | 网关被显式装配后默认 `true` | 保持；`EnableMcp=true` 是第一层授权，`mcp.autostart=false` 可进一步禁止监听 |
| `ModuleHost.EnableCommands/EnableUiModules` | 显式构造 ModuleHost 后默认启用 | 保持；构造专用宿主并调用 `Start` 已是明确选择 |
| WebGateway | 构造函数不监听；`Start` 或显式放入 ServiceComposition 后才启动 | 已符合；保持 |
| `ToolWindowDescriptor.DefaultVisible` | 已注册窗口默认可见 | 属于消费方明确注册的 UI，不是隐式后台能力；保持 |
| 日志、布局、控制台与窗口最大化手势 | 构造对应核心组件后工作 | Shell 基础能力；保持 |

## 4. 代码整改

- `ShellConfig.EnableMcp`、`EnableModules` 删除 `= true` 初始化器，默认值回归 `false`。
- `ShellWindow` 始终建立中央命令集和指令详情；MCP 关闭时独立注册 `command.list/show/domains/manual`。
- MCP 关闭时不创建 `McpGateway`、`PromptGovernanceStore`、`McpAuditRecorder`，不注册 MCP/治理命令，
  不调用 `TryAutostart`。
- 命令集在 MCP 关闭时禁用“服务状态”和治理筛选；指令详情保留 Help、参数和潜在 schema 信息，但禁用
  schema 命令和提示词治理操作。
- 演示宿主不显式打开可选能力，因此以最小配置启动；消费方示例改为按需设置开关。
- 源码版本提升到 3.0.1，避免生成与已归档 3.0.0 同版本但内容不同的包。

## 5. 消费方动作

- 需要模块命令和热重载：显式设置 `EnableModules=true`。
- 只需要 UI 模块：显式设置 `EnableUiModules=true`。
- 需要 MCP：显式设置 `EnableMcp=true`；只装配不监听时再设置 `mcp.autostart=false`。
- 需要服务端管理页的 Shell 前端：显式设置 `EnableRemoteManagementViews=true`。
- 不需要上述能力的消费方应删除冗余的 `= false` 也可保留；两种写法行为相同。

现有 OHS 已显式声明 MCP/模块/远程视图边界，WBall 已显式关闭 MCP 并开启模块，因此默认值变化不改变其
当前意图。两者均已从 3.0.1 staging 完成独立还原和回归；验证结束后已恢复正式 3.0.0 引用，未把候选源写入
消费方正式配置。

## 6. 验证门禁

- [x] 默认 `ShellConfig` 的 MCP、模块、UI 模块和远程管理视图均为关闭。
- [x] 默认 `ShellWindow.Mcp`、`Modules` 为 null，注册 `command.list`，不注册 `mcp.start` 或 `module.list`。
- [x] 默认中央命令集仍位于原生文档主区，页面头和业务中央页切换合同不退化。
- [x] Debug 编译 0 warning / 0 error，70/70 测试通过。
- [x] Release 编译 0 warning / 0 error，70/70 测试通过。
- [x] `dotnet format --verify-no-changes` 通过；四个 `PublicAPI.Unshipped.txt` 无新增公开 API。
- [x] 3.0.1 staging 生成完成：`sourceDirty=true`、漏洞审计无已知漏洞、PackageSmoke 通过，四个程序集均为
  `3.0.1.0`，四个运行包、符号包、4 份消费手册、1 份复用说明和 14 条 SHA-256 记录齐全。
- [x] OHS 使用独立缓存从 staging 还原；Debug/Release 均 0 warning / 0 error，Contracts 各 3/3，两个配置的
  11 套 Smoke 全绿，版本投影为 `OHS 2.7.7 / AppShell 3.0.1`。
- [x] WBall 使用独立缓存与隔离 artifacts 从 staging 还原；Debug/Release 均 0 warning / 0 error，Release
  `WBallVerify` 为 `VERIFY PASS`，seed 42/43 的长期确定性哈希未变化。

本轮只生成 `b-Publish/staging/3.0.1` 审核候选。源码提交 `3b82f0d6` 已按授权推送；正式发布审核通过前不执行
`Publish-AppShell.ps1 -Version 3.0.1 -Publish`，不更新 Z 级正式快照，也不创建 `v3.0.1` 标签。
