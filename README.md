# HistoryVulcan 5.3.0

HistoryVulcan 是 OneHistory 的模块宿主，负责模块注册与生命周期、通用命令总线、本地 CLI，以及模块开发和发布管线。源码版本 **5.3.0**，冻结基线和标签仍为 **5.1.2 / v5.1.2**。

桌面界面由 HistoryAurora 维护，命令工作台与快捷键由 HistoryMercury 维护，Web/MCP 网关由 HistoryPortunus 维护。宿主不包含网关监听器或旧双进程前端。跨项目合同先执行 `diana.docs.catalog`，再按返回通道读取；工具不可用时才读取正式发布的 `z-Publish/docs`。

## 当前行为

- 运行发现只读取固定 `%AppData%\HistoryVulcan\Modules` 的直属完整包，校验 manifest 和 SHA256SUMS；不扫描项目库、裸 DLL 或旧模块槽。
- 包安装、离线安装和移除采用分阶段事务。升级先复制旧 `data/`，接入失败恢复原包和原始数据；提交后清理失败只报告残留路径。
- `Attach` 失败的模块不发布命令，冷启动保留诊断并继续其他模块；热安装返回失败。内容相同且实例健康才视为幂等成功。
- `vulcan.app.get/set` 接受通用配置键，存储位置不变；读取、列举和写入回执遮蔽敏感值，已有配置不自动迁移或删除。
- `--runtime` 仅接受 `vulcan.module.list/ready/reload/install`，保留当前用户管道、握手与动作批准。
- 本版删除的公开成员登记在三份 Unshipped 与 5.3.0 批准基线中；Shipped 历史不改写。兼容变化见[模块 API](b-Office/package/HistoryVulcan_模块API.md)。

## 维护入口

| 入口 | 内容 |
| --- | --- |
| [AGENTS](AGENTS.md) | 修改边界与模块工作区规则 |
| [项目清单](project.manifest.json) | 活动目录、版本与验证命令 |
| [项目概览](b-Office/current/项目概览.md) | 组件与职责 |
| [技术合同](b-Office/current/技术合同.md) | 需求及验收测试 |
| [验证合同](b-Office/current/验证合同.md) | 本地门禁和交付边界 |
| [有效决策](b-Office/current/有效决策.md) | 有效约束与 DEC-060 |
| [文档中心](b-Office/文档中心.md) | 编辑源、历史和发布资产 |
| [模块开发手册](b-Office/package/模块开发手册.md) | 仅模块使用的开发管线 |
| [起步示例](b-Code-Samples/README.md) | 对齐现行宿主的最小模块 `DemoModule` |

## 构建与验证

根级 `HistoryVulcan.sln` 是唯一解决方案，SDK 由 `global.json` 固定。只验证本仓库，不引用邻接项目源码。

```powershell
dotnet restore .\HistoryVulcan.sln --locked-mode
dotnet build .\HistoryVulcan.sln -c Debug --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\HistoryVulcan.sln -c Release --no-restore
dotnet test .\b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj -c Release --no-build --no-restore
dotnet format .\HistoryVulcan.sln --verify-no-changes --no-restore
```

`b-Code-HistoryVulcan/` 保存源码，`b-Code-Tests/` 保存测试，`b-Code-Eng/` 保存发布输入与 API 基线。发布引擎在 Services 内实现。
`b-Office/package/` 是消费文档编辑源；`z-Publish/` 由发布流程生成，不能手改。

宿主使用 `vulcan.release.cycle`，不走模块 `vulcan.dev.*` 管线。候选构建必须显式指定工作区；省略工作区属于正式流程，需要另行授权。提交、推送、打标签、部署和真机联调不包含在本轮源码修复中。
