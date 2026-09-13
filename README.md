# HistoryVulcan 5.3.0

OneHistory 模块宿主：模块注册与生命周期、命令总线、本地 CLI、开发和发布管线。源码及冻结基线为 **5.3.0 / v5.3.0**。

| 入口 | 用途 |
| --- | --- |
| [工作合同](AGENTS.md) / [项目清单](project.manifest.json) | 修改边界、活动目录与命令 |
| [项目概览](b-Office/current/项目概览.md) | 组件与职责 |
| [文档中心](b-Office/文档中心.md) | 现行合同、消费文档与证据索引 |
| [验证合同](b-Office/current/验证合同.md) | 构建、测试与交付检查 |
| [模块 API](b-Office/package/模块API.md) / [开发手册](b-Office/package/模块开发手册.md) | 模块接入与工作区流程 |
| [起步示例](b-Code-Samples/README.md) | DemoModule |

根 `HistoryVulcan.sln` 是唯一解决方案，SDK 由 `global.json` 固定。首次构建先执行 `dotnet restore .\HistoryVulcan.sln --locked-mode`，其余命令见验证合同。

宿主使用 `vulcan.release.cycle`，禁止走模块 `vulcan.dev.start/submit/finish`。候选构建须显式指定工作区；提交、标签、推送、正式发布及部署须有明确授权。
