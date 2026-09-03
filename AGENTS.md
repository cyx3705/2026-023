# HistoryVulcan AI 工作合同

本文件适用于整个仓库。**当前源码为 5.1.3，宿主冻结基线为 5.1.2**；`HistoryVulcan.Core`
在 4.0 解冻完成收口后，已随宿主于 5.1.2 重新冻结（DEC-056）。冻结面只保留模块注册器、命令总线和开发/发布总线（模块走 `--cli vulcan.dev.start/submit/finish`；宿主禁止走开发管线）。进入项目后先确认现行合同和修改边界，
再按任务读取最小必要上下文。

## 模块开发必须走手册（Janus / Mercury / Diana / Aurora / 其它）

对话根在本仓时，只维护 HistoryVulcan 自己。**用户要开发、排查或修改其它编号模块时，必须走模块开发手册：开 F 盘工作区并把对话根迁进去。禁止留在本仓、按绝对路径改 `HistoryClio\<模块主树>`。** 那等于没走工作区，合并会改错树或锁死目录。不要等用户再提醒一次。

1. `diana.docs.catalog`，把完整输出留在对话中，再按节读
   `diana.docs.vulcan file=docs/模块开发手册.md`。只有 **Diana MCP 工具未暴露或调用失败**时，
   才能记录具体原因后降级读取目标项目正式 `z-Publish/docs`；禁止打开邻接项目的
   `b-Office/current`、`package` 或 worktree 补文档。
2. **开发管线只给模块、禁止 MCP。** 用同目录 Console CLI：`HistoryVulcan.Cli.exe --cli vulcan.dev.start project=<YYYY-NNN-模块> slug=<问题> agent=grok`。宿主禁止走这三条。改完 `--cli vulcan.dev.submit`。**禁止对话根还在正在 `finish` 的那条 `F:\ai工作区` 工作区时调用 `vulcan.dev.finish`。** grok 必须先 `move_agent_to_root` 到该模块 Clio 主树且该树在 `main`，等到迁根成功回执、对话路径已离开 F 盘，再 finish。其他 AI 不迁根；可见路径只是另一条 F 盘残留 ≠ 禁止。finish 会删工作区；对话仍钉在**那一条**上面时 Cursor 占路径删不掉，或目录已删后每次迁根都在死路径上 `git checkout --detach`，报 `not a git repository`，**这一轮对话作废，不要再迁根补救**。不要把这些指令当 MCP 工具调。
3. grok **必须**按手册四步迁进工作区：主树 `checkout --detach` → `move_agent_to_root` → 工作区切回 `ai/<项目>/<工作区名>` → 主树 `checkout main`。finish 前反向迁回主树 `main` 并确认成功。工作区目录已经没了就不要再迁根。
4. 已发布合同默认用 catalog 返回的 `diana.docs.<通道>`；不得猜通道、手写文件表或把
   `diana.docs.*` 当开发管线。读实现和改代码都在 F 盘工作区，不在 Clio 主树。
5. **不许停宿主。** 不要 `vulcan.svc.stop` / `restart`、`vulcan.app.quit`，不要杀 `HistoryVulcan.exe`。这些要人工确认，会把这一轮卡住。模块热重载不关宿主。

## 图形查看必须先走 Diana

只读观察桌面 UI（截图、布局/文字核对、重叠或空白区域检查）时，先调用 `diana.view.windows`，
再调用 `diana.view.capture handle=<窗口句柄>`，并用图像查看工具实际读取返回的 PNG。必须保留尺寸、
捕获方式和 SHA；只拿到文件路径不算完成视觉验证。Diana 后台捕获不移动鼠标、不切换前台。

**禁止为只读观察调用 Computer Use、点击窗口、移动鼠标或抢占前台。** 只有任务确实需要点击、
输入或拖拽时才使用 GUI 控制。Diana View 工具未暴露或调用失败时，先记录具体原因，不得静默
降级为 GUI 控制；任何会接管鼠标或前台的替代方案都必须先取得用户明确同意。

手册编辑源：`b-Office/package/模块开发手册.md`。

> 本文件在 3.3.2 之前长期停留在「3.0.3 已冻结、不得新增公开 API、四份 Unshipped 必须仅含
> `#nullable enable`」的表述，而实际版本早已推进到 3.3.x、Unshipped 累积了上百行。
> 陈述与事实不符会让 AI 要么被不存在的冻结挡住，要么整份忽略本文件失去全部边界约束。
> **改动版本线时必须同步本文件**，这与同步 `project.manifest.json` 同等重要。

## 启动读取顺序

1. 读取根目录 `project.manifest.json`，确认项目身份、冻结状态、活动目录和可用命令。
2. 读取根目录 `README.md` 与 `b-Office/current/项目概览.md`。
3. 根据任务读取 `技术合同.md`、`有效决策.md` 或 `验证合同.md`；涉及目录治理时读取
   `b-Office/文档中心.md`，涉及消费或跨项目复用时读取 `b-Office/package/HistoryVulcan_模块API.md`。
4. 只进入 manifest 声明的活动目录。`z-Publish/`、`bin/`、`obj/`
   和 `artifacts/` 默认不进入源码维护上下文。
5. 跨项目说明书：先执行 `diana.docs.catalog`，把完整输出留在本对话中，再调用其中一条
   `diana.docs.<通道>`。只有 **Diana MCP 工具未暴露或调用失败**时，才能记录具体原因后
   降级读取目标项目正式 `z-Publish/docs`；不要打开邻接项目仓库的 `b-Office/current`、
   `package` 或 worktree，也不要依赖手写文件表。
6. **其它模块的开发/排查**：停在本条，改走上文「模块开发必须走手册」；不要对本仓「只进入活动目录」
   做例外、去扫邻接 Clio 主树。

## 真值与冲突处理

- 用户当前指令决定任务范围，但不隐式授权提交、推送、正式发布或破坏性操作。
- 现行行为以 `b-Office/current/`、三份 `PublicAPI.Shipped.txt`、测试和源码共同判断。
- `b-Office/package/` 是消费合同编辑源；`z-Publish/` 根部是当前候选，
  `z-Publish/history/` 保存不可变发布归档。不得直接编辑生成副本。
- `b-Office/history/` 不是常用读取范围。确需版本背景时读取最小必要文件，历史结论不得覆盖
  current、测试或运行事实。
- 文档与实现冲突时必须指出冲突，不能静默选择一方并改写另一方。

## 版本线与冻结边界

- 当前开发线是 **5.1.x**，源码为 **5.1.3**，宿主冻结基线仍为 **5.1.2 / `v5.1.2`**（DEC-056）。
  5.1.3 修的是模块接入次序与就绪屏障（DEC-057），属于冻结合同 §4 第二类「正确性缺陷修复」，
  三份 Shipped 一个符号未动，新增能力全部用命令名表达。Core 在 4.0
  的阶段性解冻已结束；5.0 起宿主不再为模块提供领域抽象（DEC-052）。冻结后新增公开面必须先升版本、
  进入对应版本的 `PublicAPI.Unshipped.txt` 并通过宿主进程内公开 API 门禁；删除或签名变更必须进消费变更摘要并走主版本号。
- `v3.0.3` 是 V3 历史冻结标签，只对 3.0.x 维护分支有效：那条分支只接受致命崩溃、
  数据丢失或安全漏洞修复，且不新增公开 API。**不要把这条约束套用到 3.3.x。**
- MCP 与模块能力默认关闭，由消费方显式启用；不得恢复隐式监听、扫描或后台能力。
- CI 只执行还原、构建、测试、格式和公开 API 门禁，不执行发布脚本、打标签或部署。
- **门禁只验证本仓库自身**：解决方案与 CI 不得出现指向邻接项目的跨仓库
  `ProjectReference`。依赖其他项目实现的联调用例归各自仓库（DEC-023、DEC-040）。
  各 `YYYY-NNN-*` 目录是独立 git 仓库；本地磁盘上存在不代表属于本仓，也不代表 CI 上存在。

## 工作边界

- 修改前后检查 Git 状态，保留用户已有改动，不回退无关文件。
- 不直接编辑 `z-Publish/` 候选/正式归档或第三方依赖。
- 修改消费合同应先改 `b-Office/package/`，再由发布流程生成副本。
- 不把密钥、令牌、个人路径或机器专用状态写入仓库。
- 未经用户明确授权，不执行 Git commit、tag、push、正式发布或删除。
- 新增活动目录、外部依赖或验证命令时，同步更新 manifest 和现行文档。

## 实施与验证

- 优先沿用现有项目结构；开发管线在宿主进程内实现，不以 PowerShell 脚本作为发布引擎。每项现行要求应有可识别编号和对应验收方式。
- 先运行直接相关的快速检查，再按风险运行完整验证。
- 无法执行、未执行和失败必须明确记录，不能写成已通过。
- 完成时，源码、现行文档、manifest、测试与发布边界应相互一致。
