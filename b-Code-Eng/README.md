# HistoryVulcan 工程脚本

本目录是宿主的独立工程区：候选构建、公开 API 基线、质量门禁、项目合同和发布管线。
源码在 `../b-Code-HistoryVulcan/`（Core / Services / ServiceHost / Extensibility / App），不要把脚本再放回源码树。

| 脚本 | 用途 |
| --- | --- |
| `Build-HistoryVulcanPackage.ps1` | 生成 `z-Publish` 根候选 |
| `Assert-PublicApiBaseline.ps1` | 当前版本 Unshipped 与 `public-api-baselines/<版本>/` 比对 |
| `Test-QualityGate.ps1` | 抑制标记与 1000 行热点 |
| `Test-ProjectContract.ps1` | AI-ready / 宿主合同 |
| `Pack-HistoryVulcanInstaller.ps1` | 可选 Inno/7z 安装包 |
| `Write-TrxFailureAnnotations.ps1` | CI 失败摘要 |
| `pipeline/` | 模块发布登记表与合同内核（4.6.0 从 Diana 迁入） |
| `release/` | 消费文档清单与可选 Inno 脚本 |
