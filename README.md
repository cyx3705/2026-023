# HistoryVulcan

> OneHistory 模块宿主：模块生命周期、命令总线、CLI 与发布管线

![OneHistory Logo](./b-Office/Logo.png)

## 定位

HistoryVulcan 是 OneHistory 的模块宿主：负责模块发现、校验、装卸与热重载，命令注册与执行，
本地 Console CLI，以及模块开发管线和宿主发布管线。

- 宿主只维护通用生命周期与命令集成，不提供模块领域抽象。
- 界面、控制台与主题由 HistoryAurora 提供（登记为宿主唯一前端）；Web/MCP 传输由 HistoryPortunus 提供。

## 概况

| 项 | 值 |
| --- | --- |
| 编号 | `2026-023` |
| 角色 | 宿主（`kind=host`） |
| 指令域 | `vulcan` |
| 界面 | 无自有界面，由 HistoryAurora 提供 |
| 对外消费面 | 代码面：`HistoryVulcan.Core` 公开合同 + 命令总线 |
| 版本 | [`VulcanVersion.props`](./b-Code-HistoryVulcan/VulcanVersion.props)；冻结基线见 [冻结合同](./b-Office/current/冻结合同.md) |

## 能力

| 类 | 指令 | 用途 |
| --- | --- | --- |
| `module` | `list` / `ready` / `install` / `reload` / `unload` / `uninstall` | 模块装卸与运行状态 |
| `command` | `list` / `show` / `help` / `domains` | 命令目录 |
| `app` | `show` / `hide` / `get` / `set` / `focusconsole` | 生命周期壳命令（执行体转到 Aurora）与宿主设置 |
| `svc` | `status` / `autostart` | 后台服务 |
| `cli` | `list` / `show` | CLI 白名单 |
| `dev` | `start` / `submit` / `finish` | 模块开发管线（只走 Console CLI） |
| `release` | `cycle` | 宿主自身的发布 |

命令契约、模块包格式与 CLI 通道见 [模块 API](./b-Office/package/模块API.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [文档中心](./b-Office/文档中心.md) | 文档索引与读取顺序 |
| [项目概览](./b-Office/current/项目概览.md) | 组件与所有权 |
| [技术合同](./b-Office/current/技术合同.md) | 唯一 REQ 定义与验收方法 |
| [有效决策](./b-Office/current/有效决策.md) | 决策依据与规则索引 |
| [验证合同](./b-Office/current/验证合同.md) | 验证命令、测试边界与交付检查 |
| [模块 API](./b-Office/package/模块API.md) | 模块接入与消费语义 |
| [模块开发手册](./b-Office/package/模块开发手册.md) | 工作区、submit/finish 与模块文档规范 |
| [起步示例](./b-Code-Samples/README.md) | 最小模块 DemoModule |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryVulcan/` | 产品源码：Core、Services、ServiceHost、App、Cli |
| `b-Code-Samples/` | 起步示例，不进正式运行区 |
| `b-Code-Tests/` | 宿主测试 |
| `b-Code-Eng/` | 发布登记表、public-api 基线与发布输入 |
| `b-Office/` | 项目文档：`current/` 现行合同、`package/` 消费合同、`history/` 只读归档 |
| `z-Publish/` | 宿主快照（根部 `host/` 扁平）与 `history/` 归档，不能手改 |

## 构建与验证

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Release --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Release --no-build --no-restore
```

根 `HistoryVulcan.sln` 是唯一解决方案，SDK 由 `global.json` 固定。其余门禁命令见 [验证合同](./b-Office/current/验证合同.md)；
推送时 [`historyvulcan-freeze-gate.yml`](./.github/workflows/historyvulcan-freeze-gate.yml) 在 GitHub Actions 上复验。

## 开发与发布

宿主只走 `vulcan.release.cycle`，**禁止**走模块的 `vulcan.dev.start/submit/finish`。
候选构建须显式指定工作区；提交、标签、推送、正式发布及部署须有明确授权。

## 要点

- 运行区固定为 `%AppData%\HistoryVulcan\Modules\<模块名>`，只发现直属完整包，不扫描裸 DLL。
- `--cli` 离线组合执行；`--runtime` 只连接活宿主，仅放行 `vulcan.module.list/ready/reload/install`。
- 模块发布登记表：[`module-publish.manifest.json`](./b-Code-Eng/pipeline/module-publish.manifest.json)。

---

作者：Pinavia
