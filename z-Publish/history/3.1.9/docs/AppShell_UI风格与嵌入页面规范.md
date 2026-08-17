# AppShell UI 风格与嵌入页面规范

> 适用版本：AppShell 3.1.9 候选；当前稳定消费版本为 3.1.7，3.1.8 不受支持

本文是 AppShell 宿主、内置页面和外置 UI 模块的视觉合同。嵌入页面必须复用 AppShell 动态资源，
不得复制固定色板或在页面内维护第二套浅色/深色主题。运行时真值位于
`AppShell.Shell/Themes/ShellTokens.xaml`、`ShellTokens.Dark.xaml` 和 `ShellControls.xaml`。

## 1. 使用原则

- 页面根容器使用透明背景或 `Shell.Brush.Surface`，让宿主窗格决定外层背景、圆角和投影。
- 颜色、字体、字号、圆角、间距和控件高度统一使用 `{DynamicResource ...}`；主题切换后必须即时更新。
- 页面不绘制第二层标题栏、窗口边框、窗格卡片或关闭/最大化按钮。页签、拖动和窗口动作由 AppShell 承载。
- 页面区保持工作型界面密度：工具栏紧凑、信息可扫描，不使用营销式大标题、装饰卡片或渐变背景。
- 业务动作进入 `CommandBus`；纯选择、焦点和键盘导航可留在视图层。

## 2. 颜色令牌

| 资源键 | 浅色 | 深色 | 用途 |
| --- | --- | --- | --- |
| `Shell.Brush.Canvas` | `#F5F6F7` | `#1A1D1C` | AppShell 工作区背景 |
| `Shell.Brush.Surface` | `#FFFFFF` | `#1D201F` | 页面、窗格和弹层主表面 |
| `Shell.Brush.SurfaceAlt` | `#F5F6F8` | `#242625` | 次级区域、禁用控件背景 |
| `Shell.Brush.SurfaceHover` | `#ECEEF1` | `#2A2D2C` | 悬停背景 |
| `Shell.Brush.SurfacePressed` | `#E0E3E8` | `#343736` | 按下背景 |
| `Shell.Brush.TextPrimary` | `#1F2328` | `#E2DAC6` | 正文、标题、选中内容 |
| `Shell.Brush.TextSecondary` | `#6B7280` | `#ACA593` | 说明、元数据、次级图标 |
| `Shell.Brush.TextDisabled` | `#A1A7B0` | `#77746A` | 禁用文本 |
| `Shell.Brush.TextOnAccent` | `#FFFFFF` | `#171918` | 主题色实心控件上的文本 |
| `Shell.Brush.Accent` | `#A87A12` | `#D9A441` | 焦点、选中、主要动作 |
| `Shell.Brush.AccentHover` | `#8C650E` | `#E8B65C` | 主要动作悬停 |
| `Shell.Brush.AccentSoft` | `#FAF0D8` | `#33342A` | 选中页签、柔和强调背景 |
| `Shell.Brush.Danger` | `#C42525` | `#E06C6C` | 错误、破坏性动作 |
| `Shell.Brush.DangerSoft` | `#FBE9E9` | `#3A2320` | 错误提示背景 |
| `Shell.Brush.Warning` | `#B26A00` | `#E0A458` | 警告状态 |
| `Shell.Brush.Success` | `#1E7F4B` | `#5FBE8B` | 成功状态 |
| `Shell.Brush.Hairline` | `#E4E7EB` | `#2A2D2C` | 必要的内部细分隔线 |
| `Shell.Brush.ControlBorder` | `#D8DCE2` | `#343736` | 输入控件边框 |
| `Shell.Brush.WindowButtonHover` | `#DDE1E6` | `#2A2D2C` | 窗口按钮悬停 |
| `Shell.Brush.WindowButtonPressed` | `#CBD1D8` | `#343736` | 窗口按钮按下 |
| `Shell.Brush.CloseHover` | `#C42525` | `#C4453D` | 关闭按钮悬停 |
| `Shell.Brush.CloseHoverPressed` | `#A81E1E` | `#A83A33` | 关闭按钮按下 |

颜色规则：

- `Accent` 只表示选择、焦点和主要动作，不作为大面积页面底色。
- `Hairline` 只用于表格、分段工具条或弹层内部的必要分隔，不用于包围每个区域。
- 错误、警告、成功均使用语义令牌；不要用主题色替代状态色。
- 禁止在业务 XAML 中新增十六进制颜色；确需新增语义时先扩展浅/深两份令牌并补合同测试。

## 3. 字体与文本层级

| 资源键 | 值 | 用途 |
| --- | --- | --- |
| `Shell.Font.Family` | `Microsoft YaHei UI, Segoe UI` | 中文优先的界面字体 |
| `Shell.Font.MonoFamily` | `Consolas` | 命令、日志、路径和代码 |
| `Shell.Font.Body` | `13` | 正文、按钮、输入和表格内容 |
| `Shell.Font.Small` | `11` | 说明、时间、状态和辅助标签 |
| `Shell.Font.Mono` | `12` | 控制台与等宽内容 |

优先使用现有文本样式：

- `Shell.Text.Body`：正文，主文本色，13px。
- `Shell.Text.Secondary`：辅助正文，次级文本色。
- `Shell.Text.Caption`：辅助说明，11px。

嵌入页标题通常使用 13px `SemiBold`，紧贴其工具区；不要在紧凑工具页面使用 Hero 级标题。
按钮文本保持常规字重，只有当前选择、分组标题或关键数值使用 `SemiBold`。

## 4. 圆角、间距与固定尺寸

| 资源键 | 值 | 用途 |
| --- | --- | --- |
| `Shell.Radius.Window` | `0` | 主窗口和独立浮窗外缘 |
| `Shell.Radius.Inner` | `8` | 页面内部控件、弹层和窗格 |
| `Shell.Radius.TabTop` | `5,5,0,0` | 顶部页签 |
| `Shell.Radius.TabInner` | `5` | 页签内部元素 |
| `Shell.Space.Gap` | `1` | 窗格之间的弱间隔 |
| `Shell.Space.Pad` | `12` | 页面标准内边距 |
| `Shell.Space.PadTight` | `8` | 工具栏和紧凑区域内边距 |
| `Shell.Space.ControlPad` | `10,4` | 文本型控件内容边距 |
| `Shell.Space.TabStrip` | `3,3,3,0` | 页签相对窗格的内缩 |
| `Shell.Size.Control` | `28` | 按钮、输入框和选择器最小高度 |
| `Shell.Size.Tab` | `32` | 页签行、页面顶栏和窗口控制行高度 |
| `Shell.Size.WindowButton` | `44` | 主窗口控制按钮宽度 |

页面只能有一层 8px 内部圆角，不把卡片嵌套进卡片。固定格式控件应明确 `MinHeight`、网格列宽、
`MinWidth` 或 `MaxWidth`，动态文本必须换行或省略，不能撑动顶栏和工具栏。

## 5. 控件与交互状态

- 普通按钮：表面底色 + `ControlBorder`，高度至少 28px，圆角 8px；悬停/按下使用对应 Surface 令牌。
- 主要按钮：`Accent` 背景、`TextOnAccent` 前景；页面中同一操作组通常只有一个主要按钮。
- 图标按钮：优先使用现有 AppShell/Lucide 图标，稳定为方形命中区，并提供 ToolTip。
- 输入框、组合框：使用隐式 Shell 样式；焦点边框使用 `Accent`，禁用状态使用 `TextDisabled`。
- 表格：表头使用 `Shell.GridHeader`；行选中使用 `AccentSoft`，不要恢复系统默认浅灰模板。
- 菜单、Popup、ToolTip：表面使用 `Surface`，边框使用 `Hairline`，圆角 8px，使用 `Shell.Shadow.Flyout`。
- 列表、树和 Tab：必须使用 Shell 模板；深色主题下不得出现系统白底或黑色默认文本。

## 6. 嵌入页面结构

推荐页面结构：一条紧凑工具栏 + 单一内容区。宿主已提供窗格边距、圆角、投影、页签和顶栏，
模块页面不要重复这些层级。

```xaml
<Grid Background="Transparent">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />
        <RowDefinition Height="*" />
    </Grid.RowDefinitions>

    <Grid Margin="{DynamicResource Shell.Space.PadTight}">
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*" />
            <ColumnDefinition Width="Auto" />
        </Grid.ColumnDefinitions>
        <TextBlock Style="{DynamicResource Shell.Text.Body}"
                   FontWeight="SemiBold"
                   Text="页面标题" />
        <Button Grid.Column="1"
                Command="{Binding RefreshCommand}"
                ToolTip="刷新">
            <TextBlock Text="刷新" />
        </Button>
    </Grid>

    <Grid Grid.Row="1" Margin="{DynamicResource Shell.Space.Pad}">
        <!-- 页面实际内容 -->
    </Grid>
</Grid>
```

### 顶栏归属

- 主页面顶栏控制整个 AppShell；工具页顶栏控制工具页或独立浮窗。
- 嵌入页不创建自己的窗口最小化、最大化、关闭或拖动区。
- 页面拖出、停靠、专注和恢复由真实页签及 AppShell 命令管线负责。
- 独立浮窗仍只保留一层 AppShell 页面顶栏，模块内容不得根据浮动状态再套标题栏。

### 响应式与可访问性

- 页面在 320px 内容宽度下仍应可操作；工具栏空间不足时换行或收进菜单。
- 文本和背景必须同时检查浅色、深色；不要只验证一种主题。
- 状态不能只靠颜色表达，必须辅以文字、图标或可访问名称。
- 键盘焦点清晰可见；Tab 顺序跟随视觉顺序；纯图标按钮必须有 ToolTip 和可识别名称。

## 7. 验收清单

- 浅色、深色切换后页面无白底、黑字或固定色残留。
- 页面根部没有第二层窗格卡片、标题栏、窗口按钮或重复外边框。
- 正文、辅助文字、等宽内容分别使用 13/11/12px 合同。
- 控件高度至少 28px，顶栏/页签行为 32px，窗口按钮宽度 44px。
- 内部圆角为 8px，页签圆角为 5px，窗口外缘为直角。
- 320px、100%/125%/150% DPI 下无文本遮挡、按钮溢出或水平工具栏截断。
- 所有业务动作可从 `CommandBus` 查询并执行；视图未绕过命令管线直接改变应用状态。
