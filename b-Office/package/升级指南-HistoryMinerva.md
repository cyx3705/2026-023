# 随宿主 4.0.0 升级：HistoryMinerva

> 先读 [升级指南-总纲](升级指南-总纲.md)，本篇只列 Minerva **实测**要改的地方。
> 数据采自 2026-08-19 对 `2026-024-HistoryMinerva` 的只读扫描。

## 工作量概览

| 项 | 数量 |
|---|---|
| 涉及文件 | 2（`AssemblyView.xaml` + `Program.cs`）|
| 用到的样式键 | **18 个不同键** |
| 自建样式 | 1（`Mapping.DataGridHeader`）|
| 自建 `ControlTemplate` | 0 |
| 宿主 API 破坏 | **1 处**（`CommandSchemaExporter` 换了命名空间）|

## 一、`CommandSchemaExporter` 换了命名空间

```
b-Code-HistoryMinerva-Tests/tests/HistoryMinerva.Smoke/Program.cs:541
    var tools = new CommandSchemaExporter(registry).ExportTools();
```

该类型从 `HistoryVulcan.Core.Mcp` 移到了 **`HistoryVulcan.Extensibility.Mcp`**。

```csharp
- using HistoryVulcan.Core.Mcp;
+ using HistoryVulcan.Extensibility.Mcp;
```

若该文件同时还用 `Core.Mcp` 的其他类型（`IMcpAuditLog`、`McpExposurePolicy`、
`McpConfirmationScope`、`PromptTextIntegrity`、`GatewayAwareConfirmation`），
那些**仍在 `Core.Mcp`**，两个 using 都留着即可。

## 二、样式键改名（18 个）

```
Shell.Brush.Accent      Shell.Brush.Canvas      Shell.Brush.Hairline
Shell.Brush.Surface     Shell.Brush.SurfaceAlt  Shell.Brush.TextPrimary
Shell.Brush.TextSecondary
Shell.Button.Accent
Shell.Font.Body         Shell.Font.Family
Shell.Radius.Inner
Shell.Size.Control      Shell.Size.Tab
Shell.Space.ControlPad  Shell.Space.Gap         Shell.Space.PadTight
Shell.Text.Body         Shell.Text.Caption
```

只涉及 `AssemblyView.xaml` 与 `Program.cs` 两个文件，是四个模块里最省事的一个。

## 三、`Mapping.DataGridHeader`：本轮留着，但要知道它的处境

```
AssemblyView.xaml:13
    <Style x:Key="Mapping.DataGridHeader" TargetType="DataGridColumnHeader">
```

这是 Minerva 唯一的自建样式，而且它针对的是 **`DataGrid`**。

**`DataGrid` 明确不在组件库的主题化范围内**（Aurora REQ-UI-002）。理由是实测数据：
12 个页面里 `ListView` 用了 21 次、`GridViewColumn` 46 次，`DataGrid` 只有 2 次——
为它维护第二套表格外观不划算。

所以这条**不会**有对应的 `Aurora.*` 成员可换。两条路：

| 选项 | 说明 |
|---|---|
| **本轮保留自建**（推荐） | 改键名时把里面引用的 `Shell.*` 换成 `Aurora.*`，样式本身留着 |
| 改用 `ListView` + `GridView` | 能接入 `Aurora.GridHeader` / `Aurora.Item.Base`，但属于**重画页面**，不在本轮范围 |

选第一条时注意：样式定义内部若引用了令牌（画刷、字体、间距），那些键**也要改**——
它们在 `Style` 的 `Setter` 里，同样会被扫描命中。

> 「禁止模块自建组件」这条禁令要等 `Aurora.Segment.Toggle` 落地才生效，
> 且 `DataGrid` 场景本就是例外。本轮不用为它做任何取舍。

## 四、命令改域

Minerva 未硬编码前端命令名（扫描无命中）。检查文档：

```bash
grep -rn "vulcan\.\(ui\|log\)\." 2026-024-HistoryMinerva --include=*.md
```

Minerva 自己的 `minerva.*` 命令不受影响。

## 五、验收

- [ ] `CommandSchemaExporter` 的 using 已改为 `HistoryVulcan.Extensibility.Mcp`
- [ ] `grep -rn "Shell\.[A-Za-z.]*"` 返回空（含 `Mapping.DataGridHeader` 样式内部的令牌引用）
- [ ] Minerva 自己的构建与 Smoke 全绿
- [ ] 在 Aurora 里打开 Minerva 页面，**浅色深色各看一次**
- [ ] `AssemblyView` 的 DataGrid 表头外观未变（本轮仍是自建样式）

> Minerva 在 Aurora 下已实测可装载，`historyminerva` 中央页正常显示——
> 这是**改名前**的状态。
