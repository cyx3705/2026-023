# AppShell 发布区

`b-Publish/` 只承担两项长期职责：保存最近一次完整候选测试结果，以及保存每次正式发布时的
Z 级最小历史副本。消费项目和 AI 默认读取 `../z-Package-AppShell/`，不扫描本目录。

## 目录合同

| 路径 | 入库 | 用途 |
| --- | --- | --- |
| `current/` | 否 | 唯一一份可覆盖候选；包含构建、测试、漏洞审计、PackageSmoke、Demo、包和消费文档 |
| `history/<版本>/` | 是 | 正式发布后保存的最小快照，与该版本发布时的 Z 根结构同构 |
| `virtual/<版本>/` | 否 | 从 history 临时生成的历史链验证或回滚准备快照 |

`history/<版本>/` 只保存 `feed/` 中的运行时 `.nupkg`、当时发布的 `docs/`、
`AppShell.reuse.md`、`README.md`、`manifest.json` 和 `SHA256SUMS`。符号包、Demo、编译目录、
测试缓存和独立文档归档不进入 history。

## 发布流程

```powershell
# 覆盖重建 current，不写 Z 或 history
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.3

# 审核 current 后，先整体更新 Z，再生成 history/<版本>
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.3 -Publish

# 从 history/<版本>/feed 生成临时历史链验证快照
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 0.7.2 -VirtualPublish
```

## 不变量

- `current/` 每次候选构建整体覆盖，只保留最近一次测试历史。
- `history/<版本>/` 不得覆盖、补写或手工修改。
- 正式发布必须提升已经审核的 current，不得在 `-Publish` 阶段重新构建。
- Z 更新完成后，history 保存同一最小快照；两者文件数和 SHA-256 必须一致。
- `virtual/` 可以随时删除重建，不代表目标版本与当前消费合同已经兼容。
- 发布脚本不执行 Git commit、tag、push，也不向 NuGet.org 推送。
