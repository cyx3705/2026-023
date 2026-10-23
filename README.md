# HistoryVulcan

> OneHistory 模块宿主：模块生命周期、命令总线、CLI 与发布管线

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
| 版本 | [`VulcanVersion.props`](./b-Code-HistoryVulcan/VulcanVersion.props)；API 变更纪律见 [公开 API 合同](./b-Office/current/冻结合同.md) |

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

接入契约、模块包格式与 CLI 通道见 [模块开发手册](./b-Office/模块开发手册.md)；指令怎么调用看注册自描述（`diana.docs.catalog` / `diana.docs.read`）。

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
| [模块开发手册](./b-Office/模块开发手册.md) | 接入契约、工作区与 submit/finish、指令自描述规范、观察取证 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code-HistoryVulcan/` | 产品源码：Core、Services、ServiceHost、App、Cli |
| `b-Code-Tests/` | 宿主测试 |
| `b-Code-Eng/` | 历史 public-api 记录与发布输入 |
| `b-Office/` | 项目文档：`current/` 现行合同（含模块开发手册）、`history/` 只读归档 |
| `z-Publish/` | 宿主快照（根部 `host/` 扁平）与 `history/` 归档，不能手改 |

## 构建与验证

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Release --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Release --no-build --no-restore
```

根 `HistoryVulcan.sln` 是唯一解决方案，SDK 由 `global.json` 固定。其余门禁命令见 [验证合同](./b-Office/current/验证合同.md)；
推送时 [`historyvulcan-quality-gate.yml`](./.github/workflows/historyvulcan-quality-gate.yml) 在 GitHub Actions 上复验。

## 开发与发布

宿主只走 `vulcan.release.cycle`，**禁止**走模块的 `vulcan.dev.start/submit/finish`。
候选构建须显式指定工作区；提交、标签、推送、正式发布及部署须有明确授权。

## 要点

- 运行区固定为 `%AppData%\HistoryVulcan\Modules\<模块名>.nupkg`，只发现直属完整包，不扫描裸 DLL；随包文件通过 `PackageDirectory` 读取对应包哈希的只读内容快照。
- `--cli` 离线组合执行；`--runtime` 只连接活宿主，仅放行 `vulcan.module.list/ready/reload/install`。
- 宿主不登记模块：模块的发布描述在各模块仓 `project.manifest.json` 的 `publish` 节；宿主身份与版本由本仓 manifest 和版本源校验。

## 保留内容

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。
