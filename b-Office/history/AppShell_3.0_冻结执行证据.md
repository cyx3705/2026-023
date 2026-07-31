# AppShell 3.0 冻结执行证据

> 首轮执行：2026-07-28；最终收口：2026-07-29
> 最终冻结版本：3.0.3
> 状态：M1-M8、冻结前整改、中央主工作区、模块默认右置与主工作区比例收口均已通过；正式发布、消费方复验和冻结门禁完成

## 1. 冻结结果

- FZ-00：删除 `IDataService`、SQLite/远端数据服务、通用表窗口、`ShellConfig.DataService` 与 `db.*`。
- FZ-01：MCP 审计和提示词治理只使用文件持久化；`EnableMcp` 不再依赖数据服务。
- FZ-02：MCP/Web 默认端口按应用身份稳定派生，冲突时有界顺延；显式端口保持最高优先级。
- FZ-03：旧布局恢复后新增窗口重新采用描述符比例，不再塌缩到 1%。
- FZ-04：公开 `DockSide.Center`，中央窗口进入原生文档主区；命令集是不可隐藏、不可浮动、不可侧停靠的固定主文档；普通四边工具页可通过原生拖放嵌入中央成为页签，并可再次拖出或停回四边。
- FZ-05：前端连接自动发布命令能力目录，服务端动态代理并支持定向、多前端歧义和离线失败。
- FZ-06：长期集合、资源释放、硬编码、跨应用资源、null 降级和线程亲和完成体检；限制另见
  [运行时约束与已知限制](../package/AppShell_3.0_运行时约束与已知限制.md)。
- BASE-273-276：2.7.6 中仍有效的框架质量修复已核对并纳入 3.0.0。
- 3.0.1 将 MCP 与模块能力改为默认关闭，由消费方显式启用；本地命令集不依赖 MCP 网关。
- 3.0.2 保留模块注册器的模块修改能力，运行期模块窗口默认合并到右侧现有标签组。
- 3.0.3 合并历史同侧窗格并将侧栏总上限收紧到 50%；四包源码、程序集、NuGet 与演示宿主版本统一。

## 2. 独立项目落位

- 023 只保留 AppShell 源码、样例、文档与包交付目录。
- 已删除 023 的 OHS 桌面/服务源码、OHS 发布物、V1 历史树、OHS 暂存区与 OHS 专属文档区。
- 删除前已核对 020 副本：`stage` 的 1202 个路径存在；V1 的 782 个 Git blob 与 020 HEAD 一致。
- 根 `AppShell.sln` 只含 Core、Services、Shell、ServiceHost、App、AppShell.Tests 六个项目。
- 020 已改为四个 `PackageReference 3.0.0`，解决方案和发布脚本不再包含 AppShell 源码工程。
- 022 已改为 `OneHistory.AppShell.Shell 3.0.0`，本地源指向 023；中央舞台显式使用 `DockSide.Center`。
- 020/022 不保留 AppShell 手册副本，只链接 023 权威文档。

## 3. 框架门禁

在 023 根执行：

```powershell
dotnet restore .\AppShell.sln --locked-mode
dotnet build .\AppShell.sln -c Debug --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Debug --no-build --no-restore
dotnet build .\AppShell.sln -c Release --no-restore
dotnet test .\b-Code-AppShell\tests\AppShell.Tests\AppShell.Tests.csproj -c Release --no-build --no-restore
dotnet format .\AppShell.sln --verify-no-changes --no-restore
```

结果：

- Debug：0 warning / 0 error，76/76 tests PASS。
- Release：0 warning / 0 error，76/76 tests PASS。
- 格式门禁：PASS。
- Public API Analyzer：Debug/Release 均无诊断。
- Core、Services、Shell、ServiceHost 的 `PublicAPI.Unshipped.txt` 均为 0 条；公开面全部进入 shipped 基线。
- Claude 冻结前审查 FZR-01～05 五项阻塞全部修复；FZR-06～10、12～18、21 已收口，FZR-11 的
  process-global 多实例隔离与 FZR-19/20 按冻结边界转入 3.1；四份 `PublicAPI.Unshipped.txt` 无新增条目。
- 最终安全核查补齐 MCP Bearer 固定时长比较、握手元数据/工具名边界、协议拒绝与异常调用审计，
  并将审计客户端稳定会话身份及四个文本字段限制在固定长度内；敏感设置 `code` 的读取结果同步改为占位符。
- 命令历史只接收总线生成的脱敏 `cmd:手动` 回显；3.0 首次读取会清空无
  `# AppShell.CommandHistory.v2:redacted` 头的旧明文历史。
- OHS 隔离消费回归发现提示词完整性拒绝被总线通用异常脱敏覆盖；`mcp.desc` / `prompt.propose` 已在命令边界
  将固定的安全校验文案转换为失败结果，同时保留总线对未知异常只返回类型的兜底。
- 停靠契约覆盖原生 `LayoutDocumentPane/LayoutDocument`、命令集主文档保护、普通 `LayoutAnchorable` 拖入中央后
  保持工具页身份并可拖回、嵌入位置保存/重启恢复、早期 `0.01px` 并排拓扑迁移、WBall 无命令集场景，
  以及真实 WPF 视觉树中的主文档宽度、中央页面头常显和多页面选择。

## 4. 正式快照与候选沿革

最终正式发布以源码候选提交 `dcfc56f9` 为输入，发布提交为 `278a0cd2`。复核结果：

- 正式 manifest：`version=3.0.3`、`channel=formal`、`sourceDirty=false`、
  `compatibilityValidated=true`。
- `b-Publish` 的 9 个二进制产物与 5 个文档/复用入口、`z-Package-AppShell` 的 4 个运行包与
  4 份消费合同均存在；逐项文件大小和 SHA-256 与 manifest/校验和一致。
- Z 级 feed 恰好保留 Core、Services、Shell、ServiceHost 四个 3.0.3 运行包，没有混入旧版本。
- NuGet 漏洞审计覆盖六个项目，未发现已知漏洞；隔离 PackageSmoke、演示发布和包结构检查通过。
- 正式包、归档、Z 级快照和随包消费文档在发布提交后不再改写；冻结提交只更新内部冻结元数据。

3.0.0 首轮候选阶段在中央主工作区页面头、页面选择及普通工具页拖入/拖回修正后，执行了不带
`-Publish` 的完整候选流水线：

```powershell
.\b-Code-AppShell\eng\Publish-AppShell.ps1 -Version 3.0.0
```

结果：

- staging：`b-Publish/staging/3.0.0`。
- manifest：`channel=staging`、`sourceDirty=true`，符合未提交审核态。
- 产物：四个 `.nupkg`、四个 `.snupkg`、一个 win-x64 framework-dependent 演示 ZIP，共 9 个。
- 现行文档交付规则：四份消费合同由 `b-Office/package` 复制到版本化 `docs` 目录；四个 `.nupkg` 不重复携带消费 Markdown，仅保留包级 `PACKAGE.md`；候选流水线已校验源文档与副本 SHA-256 一致。
- NuGet 漏洞审计：0 个漏洞条目。
- 隔离 PackageSmoke：PASS；四程序集均为 `3.0.0.0`；1,533,793 bytes / 28 files。
- 演示宿主发布、包内 `PACKAGE.md`、XML 文档、nuspec 身份、manifest 与 SHA-256：PASS。
- 本轮审核候选生成时未执行 `-Publish`；它作为 OHS/WBall 消费验证和后续正式提升的唯一候选。

2026-07-29 将消费合同、内部维护文档和发布元数据拆分后，本候选已按以下规则复核：

- `b-Office/release/consumer-docs.json` 统一声明四份消费合同：API/指令手册、模块与 MCP 接入、
  运行时约束与已知限制、3.0 消费变更摘要。
- 候选流水线应先复制到 `b-Publish/staging/3.0.0/docs`，并校验副本与 `b-Office/package` 源文件的
  SHA-256；升级手册、完整版本记录、发布模板和 JSON 清单不得进入该目录。
- 使用相同 Release 构建与 pack 参数完成隔离打包。四个 `.nupkg` 必须包含 `PACKAGE.md` 且不嵌入消费 Markdown；
  staging 的 `docs/` 下必须恰好包含上述四份文档，逐文档 SHA-256 必须与 `b-Office/package` 一致。
- 正式执行 `-Publish` 时，脚本才会把同一批候选副本写入 `b-Publish` 的版本化历史归档，
  并更新 `z-Package-AppShell` 的当前正式包与精简复用入口。
- 消费验证阶段没有执行 `-Publish`，符合先审核、再消费、后发布的边界；中央布局与工具页拖入/拖回修正后的完整候选流水线已全量通过，
  不再存在旧的 `Pixel`/`Star` 契约测试阻断。

## 5. 消费方回归

最终冻结前，两个正式消费方均从 3.0.3 包完成复验：

- OneHistoryStudio 2.7.9：从 3.0.3 staging 隔离还原，Debug/Release 均 0 warning / 0 error；
  Debug/Release 共 22 次 Smoke 全部通过，运行时版本投影为 `OHS 2.7.9 / AppShell 3.0.3`。
- WBall：提交 `9cb74dc6` 将依赖固定为 `OneHistory.AppShell.Shell [3.0.3]`；从正式 Z 级 feed
  使用全新 NuGet 缓存还原，隔离 Debug/Release 均 0 warning / 0 error，格式门禁通过，
  `WBallVerify` 返回 `VERIFY PASS`，确定性哈希保持不变。
- SE2SW 不是独立冻结消费方；其 2.2.0 真实模块注册冒烟已在 3.0.2 完成：模块及 2 条指令装载、
  界面内容实例化、右侧窗口形成和同组标签合并均通过，并由人工桌面观察确认。3.0.3 未改变公开 API，
  相关回归由 76 项框架测试继续覆盖。

以下保留 3.0.0 候选阶段的完整消费回归记录，作为冻结演进审计：

### OneHistoryStudio 020

使用候选目录中的 `PackageSmoke.NuGet.Config` 与全新独立缓存
`b-Publish/staging/3.0.0/consumer-cache/ohs-final-2` 完成强制还原后：

- OHS Debug/Release：均为 0 warning / 0 error。
- Legacy ServiceHost Debug/Release：均为 0 warning / 0 error。
- Contracts：Debug/Release 均为 3/3 PASS。
- 完整 Smoke：Debug/Release 的 Wiring、VersionProjection、V213、PromptGovernance、V230、V231、V232、
  ServiceWeb、Docking、GitHubAccount、LanSingleExe 共 11 套均为 11/11 PASS。
- FZR-09 首轮消费回归暴露成功请求也累计远端地址额度，导致 ServiceWeb 的 WebSocket 握手收到 429；修正为
  “失败鉴权尝试按远端地址限流、成功请求按会话限流”后，重新生成候选并使用上述全新缓存完成 Debug/Release
  全量回归，ServiceWeb 均为 PASS。
- 首次并行执行时 Debug 的 V232 Git push 时序断言失败，而 Release 同项通过；Debug 脱离并行后串行复跑
  11/11 PASS，判定为两套 Git Smoke 并行干扰，未修改 V232 实现或断言。
- 版本投影：`OHS 2.7.6, AppShell 3.0.0`。
- `DockingSuite` 将旧的“中央文档区拒绝普通工具页”契约更新为“允许嵌入并持久化”；中央页仍是独立
  `LayoutDocumentPane`，没有用上方工作区替代。
- 真实 WPF 视觉树测试确认中央 `DocumentPaneTabPanel` 在单页和多页状态均为 `Visible`；第二个 `Center`
  页面加入同一文档组，调用 `Show` 后保持选中，不再被命令集自愈逻辑抢回焦点。中央宽度仍超过 Shell 50%。
- 真实鼠标验收完整执行“侧边页签拖出 → 浮窗拖入中央 → 中央页签再次拖出 → 浮窗停回右侧”，并断言
  `win.float`、`win.dock ... pos=center` 与停回命令可重放；Debug 稳定性复跑连续 3/3 PASS，Release 1/1 PASS。
- 真实鼠标最终停回动作每次都重新取得浮窗标题和目标页签组的实时坐标；若第一次仍悬浮，只允许重新观察后
  再做一次物理拖放，最终右侧状态、目标页签组和规范命令断言均未放宽。
- 候选测试实例已按可执行文件路径精确终止，正式安装目录中的 OHS 实例未受影响。

### WBall 022

使用 staging 显式源与全新独立缓存
`b-Publish/staging/3.0.0/consumer-cache/wball-final` 完成 Release 强制还原后：

- Release：0 warning / 0 error。
- `WBallVerify`：`VERIFY PASS`，临时 artifacts 已清理。
- v3.1 rollback：`6381A3898C0FAD65B57D43C140917A010713AA3015F601BACE14C7E5B88333F3`。
- v3.2 rollback：`E24FD280C34B54F79DAFCAE466DE299B4B76F56B69D83EF63757B96F81BF9184`。
- v3.3 seed=42：`5A458728F1A2A4296B126E1EC2F50221EC3D212393125EDFAD62EFED12F8525B`。
- v3.3 seed=43：`E3CC0CFA0E3B630DBB11372AC3F31F03DB031E144858E75D552FC1CC1C3656CA`。

## 6. 公开合同

公开 API 真值：

- `b-Code-AppShell/src/AppShell.Core/PublicAPI.Shipped.txt`
- `b-Code-AppShell/src/AppShell.Services/PublicAPI.Shipped.txt`
- `b-Code-AppShell/src/AppShell.Shell/PublicAPI.Shipped.txt`
- `b-Code-AppShell/src/AppShell.ServiceHost/PublicAPI.Shipped.txt`

基础命令组：

- 基础/应用：`help`、`cls`、`history`、`run`、`app.exit/about/get/set/opendata`、`log.level`。
- 窗口/布局：`win.list/show/hide/float/reset/max/restore/dock/ratio`、`layout.save/load/list/reset`。
- 资源/面板：`res.root/list/open/reveal/mkdir/rename/delete`、`panel.list/show/set/reload`。
- 模块：`module.list/reload/dir/open`。
- 命令目录：`command.list/show/domains/manual`。
- MCP：`mcp.start/stop/status/schema/parse`；端口、令牌、策略、确认和自启动通过 `app.get/app.set key=mcp.*` 配置。
- 治理：`mcp.desc/pending/approve/reject/apply/revert`、`prompt.get/history/diff/propose`、
  `correction.list/propose`、`incident.list/record`。
- Web/服务：`web.status/bind/token/cors/confirm`、`svc.status/stop/restart/autostart`。

`db.*` 已全部删除。命令出现在权威目录不等于允许 MCP 执行；纯 UI/前端命令默认
`AllowMcpExecution=false`，只有描述符显式允许且目标前端在线时才会进入 MCP 工具面。

## 7. 发布与冻结边界

- 人工功能检测、Claude 两轮审查、AppShell 双配置门禁、正式快照校验以及 OHS/WBall 3.0.3
  消费复验均已通过，V3 冻结条件满足。
- 3.0.3 只发布到仓库内本地 feed、版本化文档、复用入口、清单、校验和及 Z 级快照；未发布到 NuGet.org。
- 注解标签 `v3.0.3` 指向包含本证据及冻结元数据的收口提交；该提交完整包含发布提交 `278a0cd2`
  的正式资产，且不改写其包内容。
- `v3.0.3` 是 V3 最终冻结基线。冻结后仅接受致命崩溃、数据丢失或安全漏洞修复；其他功能和公开合同
  变更进入后续版本线，并重新执行完整发布与消费门禁。
