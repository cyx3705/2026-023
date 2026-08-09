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
