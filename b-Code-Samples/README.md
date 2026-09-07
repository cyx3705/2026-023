# 起步示例：DemoModule

对齐 HistoryVulcan 5.3.0 模块接入面的最小模块。抄这里开工，不要再抄旧的反射全暴露、本地 `ModuleInfoBase` 副本或 `.panel.json`。

本目录在宿主仓内，用同仓 `ProjectReference` 锁住与现行 Core 一致。复制到编号模块仓库后：

1. 把 `ProjectReference` 换成 HintPath 引用已发布的 `z-Publish/host/HistoryVulcan.Core.dll`，`<Private>false</Private>`。
2. 在宿主仓 `b-Code-Eng/pipeline/module-publish.manifest.json` 登记 `kind=module`。
3. 走 `vulcan.dev.start` / `submit` / `finish`，不要手工拷 AppData，也不要把本示例装进正式运行区。

流程见[模块开发手册](../b-Office/package/模块开发手册.md)，接口见[模块 API](../b-Office/package/HistoryVulcan_模块API.md)。
