# HistoryVulcan 源码

当前版本由 VulcanVersion.props 定义为 5.3.0；根解决方案是唯一构建入口。

- HistoryVulcan.Core：模块上下文、命令注册/执行、日志和存储合同。
- HistoryVulcan.Services：完整包发现、事务、模块生命周期、设置/日志及进程内开发发布引擎。
- HistoryVulcan.ServiceHost：组合根、宿主循环、默认确认、离线 CLI 与当前用户 runtime 管道。
- App / Cli：WinExe 和同目录 Console CLI。

界面由 Aurora、Web/MCP 由 Portunus 维护。保留 WPF 模块所需的解析和卸载，不包含旧前端或网关。
运行区仅固定 Modules 直属完整包；不扫描项目库或裸 DLL。
公开 API 由 Shipped 加 Unshipped 增删定义，5.3.0 批准基线在 b-Code-Eng；历史 Shipped 不修改。

构建与验收见[根 README](../README.md)，消费合同见[模块 API](../b-Office/package/HistoryVulcan_模块API.md)。
