# AppShell 3.0.3 主工作区比例收口

> 日期：2026-07-29  
> 修复版本：3.0.3 正式

## 问题

3.0.2 阻止了窗口缩放期间的比例污染，但侧栏总上限仍为 80%。在 OHS 实际保存的 `793px` 宽窗口中，
左栏为 `310.8px`、右栏为 `404.04px`，另有模块旧侧栏 `25px`；运行时即使归一到 80%，中央区也只剩约
`159px`，页面不会崩溃但不可正常使用。历史布局中的独立模块侧栏也没有在恢复时并入同侧标签组。

## 修复规则

- 左右侧栏合计最多占横向 50%，上下侧栏合计最多占纵向 50%。
- 中央主工作区在每个轴上至少保留 50%；当前 `793px` 宽窗口至少保留约 `396px`。
- 加载历史布局并补齐已注册窗口后，将同侧多个 `LayoutAnchorablePane` 合并为一个标签组。
- 运行期注册、手工重新停靠与历史布局恢复使用相同的“同侧单窗格”语义。
- 主窗口缩放期间仍暂停手势采样，缩放完成后按受约束的记录比例重新布局。
- 不修改 `IDockingService`、`ToolWindowDescriptor` 或 `DockSide` 公开合同。

## 验证

- [x] Docking 定向测试 24/24。
- [x] Debug 全量测试 76/76。
- [x] Release 全量测试 76/76。
- [x] `dotnet format --verify-no-changes` 通过。
- [x] 左右各请求 55% 时，侧栏合计被约束到 50%。
- [x] 两个独立右侧历史窗格重新加载后成为同一个标签组。
- [x] 主窗口缩放不会被记录为新的分隔条手势。
- [x] AppShell staging、漏洞审计和隔离 PackageSmoke 通过。
- [x] OHS 2.7.9 从 3.0.3 staging 隔离还原；Debug/Release 均 0 warning / 0 error，22 次 Smoke 全部通过，
  运行时版本投影为 `OHS 2.7.9 / AppShell 3.0.3`。
- [x] 正式 manifest 为 `channel=formal`、`sourceDirty=false`、`compatibilityValidated=true`，source commit 为
  `dcfc56f9`；正式 feed 只保留四个 3.0.3 运行包。

不执行开机自启动测试。OHS 正式发布与部署结果由 OHS 2.7.9 发布记录承接。
