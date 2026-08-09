namespace HistoryVulcan.Core.Docking;

/// <summary>
/// 工具窗口注册模型(§14.2 封装工作清单第 1 条)。
/// 派生应用只面对本模型,禁止直接引用停靠库类型。
/// </summary>
public sealed class ToolWindowDescriptor
{
    /// <summary>指令可寻址的窗口名(vulcan.ui.show name=...),要求小写、无空格、进程内唯一。</summary>
    public required string Id { get; init; }

    /// <summary>标题栏与「视图」菜单显示的标题。</summary>
    public required string Title { get; init; }

    /// <summary>
    /// 默认停靠方位。未指定时为右侧；模块仍可显式指定 Left/Top/Bottom/Center/Tab
    /// 覆盖该默认值。Center 占据中央工作区；Tab 时须同时指定 <see cref="DefaultTabTarget"/>。
    /// </summary>
    public DockSide DefaultSide { get; init; } = DockSide.Right;

    /// <summary>四边停靠时默认占主窗体的比例，须严格位于 (0,1)；Center/Tab 布局不使用该值。</summary>
    public double DefaultRatio { get; init; } = 0.25;

    /// <summary>DefaultSide 为 Tab 时,并入哪个窗口所在的标签组(填对方 Id)。</summary>
    public string? DefaultTabTarget { get; init; }

    /// <summary>是否默认可见;false 表示注册后先隐藏,由指令或菜单唤出。</summary>
    public bool DefaultVisible { get; init; } = true;

    /// <summary>是否单例(W-11;首版仅单例生效,多实例开关预留)。</summary>
    public bool IsSingleton { get; init; } = true;

    /// <summary>
    /// 窗口内容工厂。返回值在 WPF 宿主中应为 FrameworkElement;
    /// 声明为 object 以保持 Core 层不依赖 WPF。
    /// 例外:Id 为 "console" 的窗口内容由 Shell 提供(§4.4 标准控制台),
    /// 可不设工厂;其余窗口必须提供。
    /// </summary>
    public Func<object>? ContentFactory { get; init; }
}
