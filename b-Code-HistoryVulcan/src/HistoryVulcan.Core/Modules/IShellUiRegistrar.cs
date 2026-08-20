using HistoryVulcan.Core.Docking;

namespace HistoryVulcan.Core.Modules;

/// <summary>模块向宿主注册内嵌界面的唯一门面。</summary>
public interface IShellUiRegistrar
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    bool IsUiThread { get; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void Invoke(Action action);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterToolWindow(string id);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterOwner(string owner);
}

/// <summary>
/// 由模块**反向提供**界面注册入口。
///
/// 与 <see cref="IShellUiAware"/> 方向相反：那个是宿主把注册器注入给消费方，
/// 本接口是承载界面的那个模块把注册器交给宿主，宿主再转给其余消费方。
/// 界面整体成为模块之后（Aurora DEC-008），宿主自己不再持有任何界面实现，
/// 注册器只能来自模块。
///
/// 宿主在 <c>CreateUi</c> 阶段先取遍提供方再分发给消费方——模块装载顺序不可控，
/// 靠目录名排序碰巧让提供方排在前面是不能依赖的。
/// </summary>
public interface IShellUiProvider
{
    /// <summary>本模块提供的界面注册入口；界面尚未就绪时为 null。</summary>
    IShellUiRegistrar? ShellUi { get; }
}
