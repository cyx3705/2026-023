# AppShell 3.0.2 模块注册窗口右侧合并整改

> 发现日期：2026-07-29
> 影响版本：3.0.0 / 3.0.1 源码基线
> 修复版本：3.0.2 候选
> 状态：实现、全量门禁、OHS 消费审核与正式发布完成；未创建标签

## 1. 问题

Shell 建立默认布局时，会把同一 `DockSide` 的窗口合并进一个 `LayoutAnchorablePane`。但模块装载发生在
`DockingHost.Initialize` 之后，运行期 `RegisterWindow` 最终调用 `PlaceAtSide`，旧实现无条件创建新的
`LayoutAnchorablePane`。因此模块以常规 `DockSide.Right` 注册 UI 时，右侧已经存在“指令详情/模块管理”
标签组，框架仍会再切出一块右侧窗格，形成窗口内重复分区。

主窗口缩放还存在另一条比例污染路径：AvalonDock 在重排完成前继续保留旧像素宽度，布局事件可能把这些
瞬时像素值除以新的宿主尺寸并误记成用户拖动比例。OHS 实际复现中，恢复窗口宽度为 `793px`，保存布局却
同时保留左栏 `310.8px`、右栏 `404.04px` 和另一侧栏 `25px`，中央列只剩约 `53px`，其星级宽度还被放大为
`181304288.63654545*`。移动页面、最大化和还原会持续放大这种污染。

## 2. 整改

- 运行期向左、右、上、下侧放置窗口时，先查找同侧现有的可见非浮动窗格。
- 找到同侧窗格后，将新窗口直接加入其标签集合并选中新标签。
- 同侧完全不存在窗格时，才按声明比例创建新窗格。
- `RegisterWindow`、模块 `IShellUiRegistrar.RegisterToolWindow`、窗口复位和 `win.dock` 共用该规则。
- 模块未声明 `DefaultSide` 时由 AppShell 默认右置；模块仍可显式改为其他方位。右置模块无需知道标准窗口 Id，
  也无需人工填写 `DefaultTabTarget`。
- 中央文档、显式 `DockSide.Tab`、浮动窗口、owner 回收和保存布局合同不变。
- 主窗口 `SizeChanged` 期间停止布局手势采样；AvalonDock 重排并按既有比例重新施加尺寸后，才重建基线。
- 同一轴上的全部侧栏合计最多占宿主 `80%`，至少为中央工作区保留 `20%`；左右与上下分别计算。
- 拖动分隔条、`win.dock`、`win.ratio`、窗口缩放和布局恢复统一经过比例归一。
- 已保存的超配像素布局在下次加载时自动压回合法范围，不要求消费者删除布局文件。

## 3. 验证

- [x] 新增运行期右侧模块窗口，与“指令详情/模块管理”共用唯一右侧窗格。
- [x] 新模块窗口注册后成为右侧选中标签，`ToolWindowInfo.Side` 仍为 `Right`。
- [x] `ResetWindow` 后仍回到同一右侧窗格，不重新分裂侧栏。
- [x] 超配左右侧栏自动归一并保留至少 20% 中央工作区。
- [x] 主窗口从 1000px 缩到 620px 不会被识别为新的分隔条手势，左右比例保持稳定。
- [x] 保存左右各 480px 的历史坏布局后重新加载，侧栏合计自动恢复到 80% 以内。
- [x] Debug 构建 0 warning / 0 error；Docking 定向测试 22/22、全量测试 74/74 通过。
- [x] Release 构建 0 warning / 0 error；全量测试 74/74 通过。
- [x] `dotnet format --verify-no-changes` 与公开 API 基线检查通过。
- [x] 3.0.2 staging 生成完成：`sourceDirty=true`、漏洞审计无已知漏洞、PackageSmoke 通过，四个程序集均为
  `3.0.2.0`，四个运行包、符号包、4 份消费手册、1 份复用说明和 14 条 SHA-256 记录齐全。
- [x] OHS 2.7.7 从 staging 独立还原 AppShell 3.0.2；Debug/Release 均 0 warning / 0 error，11 套
  Smoke 全部通过，运行时版本投影为 `OHS 2.7.7 / AppShell 3.0.2`。

源码提交 `8cf1bffb` 后已重新生成 `sourceDirty=false` 的 staging，并复用该审核候选正式提升到
`z-Package-AppShell`；正式 feed 当前只保留四个 3.0.2 运行包，3.0.0 已进入 `b-Publish` 历史归档。
本次未创建标签。

## 4. SE2SW 真实模块注册冒烟（2026-07-29）

本轮使用 AppShell `3.0.2`（提交 `55c74edb`）和 SE2SW `2.2.0` 既有 Release 产物，在独立数据目录
`%AppData%/AppShell-SE2SW-Smoke` 中显式开启模块能力；未读取或修改 OHS 正式模块目录。

- [x] SE2SW DLL 与 `ui=true` 清单装载成功：`module.list` 显示 1 个模块、2 条指令。
- [x] SE2SW 界面实例化成功且内容非空：可读取 OHS 兼容/外界模式、目录选择、重新扫描、转换选项、
  结果表格等完整控件。
- [x] `win.list` 的模型状态显示 `commanddetail`、`modules`、`motor`、`se2sw` 均为“停靠·右 32%”；
  UI 自动化树也将四者列为同一个 `LayoutAnchorablePane` 的标签。
- [x] 人工桌面观察确认右侧可见 SE2SW 窗口；与 `win.list`、UI 自动化树的停靠结果一致，真实形成窗口通过。
- [x] 显式执行 `win.show name=se2sw` 返回“已显示”，窗口保持稳定，未生成第二个嵌套侧栏或独立窗口。
- [x] AppShell 保留模块通过 `DefaultSide` 修改停靠位置的能力，同时把未声明位置的模块默认放到右侧。
  SE2SW 已删除原 `Top/0.75` 显式覆盖，直接消费 AppShell 的 `Right/0.25` 默认值。
- [x] SE2SW 已从失效的 OHS 内嵌 AppShell 源码引用切换为正式 `OneHistory.AppShell.Core 3.0.2` 包引用，
  使用 AppShell 正式 feed 独立还原，不再依赖早期复制底座目录。
- [x] AppShell 当前候选全量测试 76/76；默认右置与模块显式覆盖、运行期默认模块加入右侧标签组两条定向测试
  2/2 通过；`dotnet format --verify-no-changes` 通过。
- [x] SE2SW Release 使用正式包独立还原并构建成功，0 warning / 0 error。

说明：自动化的离屏 `PrintWindow` 捕获只得到中央表面，未包含桌面上实际可见的右侧窗格；该捕获结果与人工观察、
窗口模型及 UI 自动化树均不一致，因此判定为取证方式限制，不作为产品缺陷证据，也不随文档保留误导性截图。

结论：AppShell 3.0.2 对 SE2SW 的真实注册、内容实例化和右侧标签合并冒烟通过；未发现重复窗口或空白内容。
