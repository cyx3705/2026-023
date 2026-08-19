# 随宿主 4.0.0 升级：HistoryMercury

> 先读 [升级指南-总纲](升级指南-总纲.md)，本篇只列 Mercury **实测**要改的地方。
> 数据采自 2026-08-19 对 `2026-021-HistoryMercury` 的只读扫描。

## 工作量概览

| 项 | 数量 |
|---|---|
| 涉及文件 | 9 |
| 用到的样式键 | **28 个不同键**（四个模块里最多）|
| 自建样式 / 模板 | **0** |
| 宿主 API 破坏 | **有，4 个文件**（见第一节，必须先修）|

Mercury 用键最多但**零自建**——它是组件库的模范消费方。真正的工作量在 API 那一处。

## 一、编译会直接失败：`HistoryVulcan.Shell.Mcp` 不存在了（先修这个）

Mercury 有 4 处 `using HistoryVulcan.Shell.Mcp;`：

```
b-Code-MercuryDock/CommandSurface/CommandCatalogSession.cs:5
b-Code-MercuryDock/CommandSurface/CommandDetailView.xaml.cs:6
b-Code-MercuryDock/CommandSurface/McpToolsView.xaml.cs:3
b-Code-Tests/HistoryMercury.Smoke/Program.cs:614   （全限定名写法）
```

**`HistoryVulcan.Shell` 程序集已经不存在**——前端整体迁往 HistoryAurora。
但你要的那几个类型并没有随前端走，它们在 REQ-A1 时就已经移进了 `Services`：

| 类型 | 4.0.0 位置 |
|---|---|
| `CommandCatalogRow` | `HistoryVulcan.Services.Mcp` |
| `CommandCatalogDetail` | 同上 |
| `CommandDomainInfo` | 同上 |
| `CommandParameterInfo` | 同上 |

改法：

```csharp
- using HistoryVulcan.Shell.Mcp;
+ using HistoryVulcan.Services.Mcp;
```

`Program.cs:614` 用的是全限定名，改成 `HistoryVulcan.Services.Mcp.CommandCatalogRow`。

> 这些类型当初在 `Shell` 是历史包袱：它们是**命令目录的数据模型**，与界面无关，
> 却因为最早在前端用到而住在前端程序集里。A1 把它们移进 `Services` 正是为了
> 让服务层不依赖前端——Mercury 这次的改动，本质上是跟上那次归位。

## 二、样式键改名（28 个）

```
Shell.Brush.Accent          Shell.Brush.AccentSoft      Shell.Brush.ControlBorder
Shell.Brush.Hairline        Shell.Brush.Surface         Shell.Brush.SurfaceAlt
Shell.Brush.TextPrimary     Shell.Brush.TextSecondary
Shell.Button.Accent         Shell.Button.Ghost
Shell.Font.Body             Shell.Font.Family           Shell.Font.Mono
Shell.Font.MonoFamily       Shell.Font.Small
Shell.GridHeader            Shell.Item.Base
Shell.Radius.Inner          Shell.Shadow.Flyout
Shell.Segment.Bar           Shell.Segment.Button        Shell.Segment.ComboBox
Shell.Segment.Divider       Shell.Segment.Label         Shell.Segment.TextBox
Shell.Space.ControlPad      Shell.Space.PadTight
Shell.Text.Body             Shell.Text.Caption
```

涉及文件：`CommandDetailView.xaml(.cs)`、`McpToolsView.xaml(.cs)`、
`MercuryManagerPage.xaml`、`DockTheme.cs`、`SuggestBox.cs`、`Program.cs`。

### 注意：`Shell.Mcp.*` 不要当成样式键改

扫描 `Shell.` 会同时命中第一节那些**类型引用**（`Shell.Mcp.CommandCatalogRow` 等）。
它们要改成 `Services.Mcp`，不是 `Aurora.Mcp`——**`Aurora.Mcp` 这个命名空间不存在**。

安全顺序：**先做第一节的 using 改动，再做本节的键改名**。第一节做完后
`Shell.Mcp` 就不会再出现在扫描结果里，剩下的才全是样式键。

## 三、`DockTheme.cs` 值得单独看一眼

它是 Mercury 自己的停靠主题适配层，用了 `Shell.Brush.*` 与 `Shell.Radius.Inner`。
改名后确认扩展坞（dock.manager 窗口）在浅色深色下都正常——它不像业务页那样显眼，
容易漏看。

## 四、命令改域

Mercury 未硬编码前端命令名（扫描无命中）。检查文档：

```bash
grep -rn "vulcan\.\(ui\|log\)\." 2026-021-HistoryMercury --include=*.md
```

Mercury 自己的 `mercury.*` 命令不受影响。

## 五、全局快捷键：已经是对的，不用动

扫描命中 `IGlobalShortcutHost` 两处，都在**注释**里，且内容已经是新架构的说明
（"宿主不再预扫描模块 DLL 去寻找 IGlobalShortcutHost 实现，也不再驱动注册"）。
Mercury 已按 `<域>.hotkey.*` 命令自持快捷键，本轮无需改动。

## 六、验收

- [ ] 4 处 `HistoryVulcan.Shell.Mcp` 已改为 `HistoryVulcan.Services.Mcp`，编译通过
- [ ] `grep -rn "Shell\.[A-Za-z.]*"` 返回空
- [ ] Mercury 自己的构建与 Smoke 全绿
- [ ] 在 Aurora 里打开命令集与命令详情页，**浅色深色各看一次**
- [ ] 扩展坞（dock.manager）外观正常
- [ ] 命令目录能列出全部六个域（aurora / diana / janus / mercury / minerva / vulcan）

> Mercury 在 Aurora 下已实测可装载，dock.manager 窗口正常注册——
> 这是**改名前**的状态。第一节的 API 改动不做，Mercury 根本编译不过。
