# HistoryVulcan 源码导航

版本源为 `VulcanVersion.props`，构建入口为根 `HistoryVulcan.sln`。组件职责见[项目概览](../b-Office/current/项目概览.md)。

| 职责 | 实现位置 |
| --- | --- |
| 命令执行、绑定与审计 | Core/Commands：CommandBus、CommandRequest、CommandArguments、SensitiveName |
| 模块生命周期 | Services/Modules：ModuleHost 的 Discovery、Startup、Snapshot、Descriptors、LoadContext、Reload、Packages 分部 |
| 开发与发布 | Services/Development：ReleaseCatalog 解析登记表，ToolProcess 执行工具进程 |
| 宿主与离线组合 | ServiceHost：ServiceComposition 持有 DevelopmentContext，CommandLineRunner 先校验再装配 |
| 可执行入口 | App / Cli |

源码工程统一 `IsPackable=false`；发行形态见 [PACKAGE](PACKAGE.md)，行为约束见[技术合同](../b-Office/current/技术合同.md)，构建测试命令见[验证合同](../b-Office/current/验证合同.md)。
