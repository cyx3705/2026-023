# HistoryVulcan 工程脚本

本目录是宿主的独立工程区：历史公开 API 记录、可选安装包脚本与 CI 诊断。
源码在 `../b-Code-HistoryVulcan/`。宿主打包（构建候选、合同、质量门禁、公开 API 一致性）
在宿主进程内实现，入口是 `--cli vulcan.release.cycle`（不是模块开发管线），不再经 PowerShell 引擎脚本。

| 路径 | 用途 |
| --- | --- |
| `public-api-baselines/<版本>/` | 历史 Unshipped 与 PreFreeze 记录；DEC-076 起不参与门禁，无须按新版本复制 |
| `Pack-HistoryVulcanInstaller.ps1` | 可选 Inno/7z 安装包 |
| `Write-TrxFailureAnnotations.ps1` | CI 失败摘要 |
| `release/` | 可选 Inno 脚本 |
