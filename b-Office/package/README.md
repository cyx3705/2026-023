# HistoryVulcan 消费合同源（内部索引）

稳定读取顺序和发布边界见 [复用说明](复用说明.md)。本文件保留原发布源索引职责。

本目录只保存面向消费方的 HistoryVulcan 合同文档编辑源。它是 023 内部源目录，不是消费者运行时目录；发布副本不得直接修改。

本目录维护 HistoryVulcan **3.3.0** 正式消费合同（3.2.0 起产品由 AppShell 改名为 HistoryVulcan，包 ID 为
`OneHistory.HistoryVulcan.*`）。3.3.0（DEC-022）内置命令硬切为 `vulcan.<类>.<方法>`，命令集列为域|类|方法，
全局快捷键与命令工作台由 HistoryMercury 4.1.0 拥有。3.1.8 是不受支持的内部过渡版本。
当前正式宿主位于 `../../z-HistoryVulcan/`，运行入口为 `host/HistoryVulcan.exe`。候选位于
`../../z-Publish/current/`，审核通过后整体部署到正式快照。本目录中的 NuGet/API 文档仍服务于需要嵌入框架的消费方，
不代表宿主部署会生成或发布 NuGet 包。

## 文档流向

```text
b-Office/package/
  -> z-Publish/current/docs/
  -> z-HistoryVulcan/docs/（当前正式快照；3.1.9 及以前位于旧名 z-Package-AppShell/docs/）
  -> z-Publish/history/<版本>/docs/（正式发布后）
```

`z-HistoryVulcan` 同时保留当前正式包、精简复用说明和当前版本消费合同，是其他项目与 AI
可稳定索引的当前快照入口；旧名 `z-Package-AppShell` 的 3.1.9 快照已随 3.2.0 发布退役。这些文件均由发布脚本整体生成，不得手工修改。历史版本的消费合同以
`z-Publish/history/<版本>/docs/` 为准。

## 消费合同

- [HistoryVulcan UI 风格与嵌入页面规范](HistoryVulcan_UI风格与嵌入页面规范.md)：颜色、字体、字号、圆角、间距、控件和嵌入页布局合同。
- [HistoryVulcan API 与指令手册](HistoryVulcan_API与指令手册.md)：包选择、公开 API、**命令总线如何消费**、命令合同和最小宿主。
- 模块开发流程见 HistoryDiana 的《模块开发手册》（`diana.docs.diana`）；宿主侧的接口事实仍以本目录其余文档为准。
- [运行时约束与已知限制](HistoryVulcan_运行时约束与已知限制.md)：消费方必须遵守的运行时限制。
- [消费变更摘要](HistoryVulcan_消费变更摘要.md)：只保留影响消费者的版本变化（含 3.3.0 DEC-022）。

发布清单和生成模板位于 `../../b-Code-HistoryVulcan/eng/release/`；发布步骤见
`../current/项目概览.md`，完整版本记录位于 `../history/`。新增或改名消费手册时，必须同时更新
`../../b-Code-HistoryVulcan/eng/release/consumer-docs.json`、根 `project.manifest.json` 和本索引；不得直接编辑 `z-Publish/current/docs`
或 `z-Publish/history/<版本>/docs` 下的生成副本。
