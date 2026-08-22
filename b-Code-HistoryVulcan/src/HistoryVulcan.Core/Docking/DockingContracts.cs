// 停靠契约：宿主与界面模块之间关于「窗口摆在哪」的全部约定。
//
// ToolWindowInfo / ToolWindowDescriptor / DockSide / StandardWindowIds /
// ShellCommandEventArgs / IDockingService —— 它们此前是五个文件。
// 拆开不携带任何信息：描述符引用 DockSide，服务收描述符，事件由服务抛出，
// 标准 Id 是服务的取值域。改其中一个几乎总要连带看其余几个。
//
// 实现方是承载界面的模块（HistoryAurora）。宿主自 4.0.0 起不含任何界面实现，
// 这里只有契约。

namespace HistoryVulcan.Core.Docking;

/// <summary>某个工具窗口的当前状态快照(vulcan.ui.windows 的数据源)。</summary>
public sealed record ToolWindowInfo(
    string Id,
    string Title,
    bool IsVisible,
    bool IsFloating,
    DockSide? Side,
    double? Ratio,
    string Owner = "framework");

/// <summary>
/// 停靠系统对外唯一门面(§14.2 封装原则):
/// 四类标准窗口与派生应用只通过本接口操作窗口与布局,
/// 未来 win.* / layout.* 指令(M2)也落在本接口上。
/// </summary>
public interface IDockingService
{
    /// <summary>当前最大化的工具窗口 Id;未最大化时为 null。</summary>
    string? MaximizedId { get; }

    /// <summary>列出全部已注册窗口及状态(vulcan.ui.windows)。</summary>
    IReadOnlyList<ToolWindowInfo> ListWindows();

    /// <summary>显示窗口(vulcan.ui.show);若已隐藏则唤出,已显示则激活。</summary>
    void Show(string id);

    /// <summary>隐藏窗口(vulcan.ui.hide);状态保留,不销毁(§4.1 关闭=隐藏)。</summary>
    void Hide(string id);

    /// <summary>浮动为独立顶层窗口(vulcan.ui.float)。</summary>
    void Float(string id);

    /// <summary>停靠到指定方位(vulcan.ui.dock);Center 占中央工作区，Tab 并入 targetId 标签组。</summary>
    void Dock(string id, DockSide side, double? ratio = null, string? targetId = null);

    /// <summary>调整窗口占主程序窗体的比例(vulcan.ui.ratio)。</summary>
    void SetRatio(string id, double ratio);

    /// <summary>把单个窗口复位到注册时声明的默认位置(视图菜单「复位」,W-02)。</summary>
    void ResetWindow(string id);

    /// <summary>整体重置为默认布局(vulcan.ui.layoutreset,W-07)。</summary>
    void ResetLayout();

    /// <summary>保存当前布局为命名方案(vulcan.ui.layoutsave,W-08)。</summary>
    void SaveLayout(string name);

    /// <summary>加载命名布局方案(vulcan.ui.layoutload);失败返回 false。</summary>
    bool LoadLayout(string name);

    /// <summary>列出全部命名布局方案(vulcan.ui.layouts)。</summary>
    IReadOnlyList<string> ListLayouts();

    /// <summary>运行期注册工具窗口。owner 是模块热重载时的回收键。</summary>
    void RegisterWindow(ToolWindowDescriptor descriptor, string owner);

    /// <summary>运行期注销工具窗口;未注册时静默忽略。</summary>
    void UnregisterWindow(string id);

    /// <summary>回收 owner 名下全部工具窗口。</summary>
    void UnregisterOwner(string owner);

    /// <summary>最大化指定工具窗口。</summary>
    void MaximizeWindow(string id);

    /// <summary>退出临时最大化状态并恢复进入前的完整布局。</summary>
    void RestoreLayoutFromMaximized();

    /// <summary>
    /// 布局侧产生的等价指令(W-10):用户拖拽等手势结束后,
    /// 封装层生成 win.* / layout.* 指令文本并经此事件上报。
    /// M2 指令总线接入后订阅本事件完成回显与落日志;
    /// 由指令/API 引发的布局变更不会再回声成新事件(§14.2 防再入)。
    /// </summary>
    event EventHandler<ShellCommandEventArgs>? CommandGenerated;

    /// <summary>窗口集合或最大化状态变化时触发。</summary>
    event EventHandler? WindowsChanged;
}

/// <summary>
/// 工具窗口相对主程序窗体的停靠方位(§4.1 W-03 五种落点)。
/// </summary>
public enum DockSide
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Left = 0,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Right = 1,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Top = 2,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Bottom = 3,
    /// <summary>并入目标标签组(vulcan.ui.dock pos=tab target=...)。</summary>
    Tab = 4,
    /// <summary>占据中央工作区；多个中央窗口组成标签组。</summary>
    Center = 5,
}

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

/// <summary>Window identities owned by HistoryVulcan and available to derived hosts.</summary>
public static class StandardWindowIds
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string Console = "console";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string Mcp = "mcp";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string CommandDetail = "commanddetail";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string Modules = "modules";
}

/// <summary>
/// 一条等价指令的文本表达(§5.1 语法),及其来源类别(C-01 来源标签)。
/// </summary>
public sealed class ShellCommandEventArgs : EventArgs
{
    /// <summary>指令文本,如 "vulcan.ui.dock name=console pos=bottom ratio=0.28"。</summary>
    public required string CommandText { get; init; }

    /// <summary>来源类别:"layout"(拖拽手势)、"UI"(菜单/按钮)等。</summary>
    public required string Source { get; init; }
}
