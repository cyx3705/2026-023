# 随宿主 4.0.0 升级：HistoryJanus

> 先读 [升级指南-总纲](升级指南-总纲.md)，本篇只列 Janus **实测**要改的地方。
> 数据采自 2026-08-19 对 `2026-020-HistoryJanus` 的只读扫描。

## 工作量概览

| 项 | 数量 |
|---|---|
| 涉及文件 | 12（7 个 XAML + 5 个 .cs）|
| 用到的样式键 | **12 个不同键** |
| 自建样式 | 1（`OperationSegment`）|
| 自建 `ControlTemplate` | 4 |
| 宿主 API 破坏 | **0**（见下） |

Janus 是四个模块里**自建最多**的一个，也是唯一因缺件而自造 `RadioButton` 模板的。

## 一、样式键改名（必做）

用到的 12 个键全部只需换前缀：

```
Shell.Brush.Accent          Shell.Brush.AccentSoft      Shell.Brush.ControlBorder
Shell.Brush.Hairline        Shell.Brush.Surface         Shell.Brush.SurfaceAlt
Shell.Brush.SurfaceHover    Shell.Brush.TextDisabled    Shell.Brush.TextPrimary
Shell.Brush.TextSecondary   Shell.GridHeader            Shell.Item.Base
```

涉及文件：

```
b-Code-*/…/BranchHistoryView.xaml      GitHubConnectionView.xaml
GraphView.xaml   GraphView.xaml.cs     HistoryPreviewDialog.xaml
OverviewView.xaml                      ProjectOperationsView.xaml
RollbackMessageDialog.xaml
```

`.cs` 里也有键引用（`GraphView.xaml.cs`），别只改 XAML。

用总纲第一节给的正则替换，避开 `IShellUiAware` 这类类型名。

## 二、自建组件：本轮**先留着**

Janus 的自建全部集中在两个文件：

| 文件 | 自建物 | 为什么存在 |
|---|---|---|
| `ProjectOperationsView.xaml:30` | `Style x:Key="OperationSegment"`（RadioButton）+ 其 `ControlTemplate` | **分段家族缺 Toggle 成员** |
| `OverviewView.xaml:33` | `ControlTemplate TargetType="ListViewItem"` + Triggers | 行样式定制 |

`OperationSegment` 是 `Aurora.Segment.Toggle` 这个缺件的**唯一实证来源**——
Aurora 的组件计划把它列为 P0，且是「禁止模块自建」这条禁令的解禁前置。

**本轮不要删这两处**。等 Aurora 补齐 Toggle 会另行通知，届时：

```xml
<!-- 补齐后应改为 -->
<RadioButton Style="{DynamicResource Aurora.Segment.Toggle}" Content="…" />
```

删掉自造版本后外观不应变化——这是那次交付的验收标准。

`OverviewView` 的 `ListViewItem` 模板要看 `Aurora.Item.Base` 能否覆盖你的触发器需求；
若不能，那是另一个缺件，请提给 Aurora 而不是继续自造。

## 三、命令改域：代码 0 处，文档 2 处（2026-08-20 重扫）

> 本篇初版的扫描命令模式漏了 `vulcan.command.*` 且只搜 `*.md`。用正确模式重扫后，
> **Janus 的代码依然是干净的**——初版结论碰巧没错，但当时的扫描不足以支撑它。

重扫命中 2 处，都在决策记录里：

```
b-Office/current/有效决策.md:375   `vulcan.ui.layoutreset`
b-Office/current/有效决策.md:406   `vulcan.ui.show name=github`
```

**建议不要改这两处。** 它们是已接受决策的历史记录，记的是当时的事实；
按仓库合同，`b-Office/current/有效决策.md` 里的既往条目不因外部改名而重写。
若担心有人照着敲，可在该条目下补一行"4.0.0 起该命令为 `aurora.ui.*`"，
而不是修改原文。

Janus 自己的 `janus.*` 命令不受影响；`vulcan.command.list` 等**保留不改**
（宿主实现，见总纲第三节）。

### 重扫命令

```bash
grep -rnE "vulcan\.(ui|log)\.|vulcan\.app\.(about|opendata|theme|window)|vulcan\.command\.(copyexample|history)" 2026-020-HistoryJanus --include=*.cs --include=*.xaml --include=*.md --include=*.json --include=*.ps1 | grep -v "/obj/\|/bin/"
```

代码部分应返回空。

## 四、宿主 API：无需改动

扫描命中的三项都是**假阳性**，确认过不用动：

| 命中 | 实际情况 |
|---|---|
| `WebGateway` | 只出现在 `TestArchitectureSuite.cs:115` 的一个**字符串字面量名单**里，不是类型引用 |
| `IUiModule` / `IShellUiAware` | 本轮不变，C 阶段才删除 |
| `Core.Mcp` | 用的是仍然保留的类型（`IMcpAuditLog` 等），未触及已删除的提示治理 |

`TestArchitectureSuite.cs` 那个名单若是在断言「宿主有哪些组件」，
升级后可能需要更新——**宿主已无 `Shell`，且 `WebGateway` 仍在**。跑一次测试即可确认。

## 五、验收

- [ ] `grep -rn "Shell\.[A-Za-z.]*" 2026-020-HistoryJanus --include=*.xaml --include=*.cs` 返回空
- [ ] Janus 自己的构建与 Smoke 全绿
- [ ] 在 Aurora 里打开三个页面（项目总览 / 分支图谱 / 项目操作），**浅色深色各看一次**
- [ ] `ProjectOperationsView` 的分段切换器外观未变（本轮仍是自造版本）
- [ ] `OverviewView` 的列表行选中/悬停态正常

> Janus 在 Aurora 下已实测可装载，三个窗口（overview / graph / projops）全部正常显示——
> 这是**改名前**的状态。改名后若某个页面变灰变丑，就是漏了某个键。
