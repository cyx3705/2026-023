# HistoryVulcan 工程脚本

本目录是宿主的独立工程区：公开 API 基线、可选安装包脚本、发布登记表。
源码在 `../b-Code-HistoryVulcan/`。宿主打包（构建候选、合同、质量门禁、公开 API 基线）
在宿主进程内实现，入口是 `--cli vulcan.release.cycle`（不是模块开发管线），不再经 PowerShell 引擎脚本。

| 路径 | 用途 |
| --- | --- |
| `pipeline/module-publish.manifest.json` | 可发布模块与宿主登记表 |
| `public-api-baselines/<版本>/` | 当前版本已批准的 Unshipped 基线 |
| `Pack-HistoryVulcanInstaller.ps1` | 可选 Inno/7z 安装包 |
| `Write-TrxFailureAnnotations.ps1` | CI 失败摘要 |
| `release/` | 消费文档清单与可选 Inno 脚本 |
