using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Extensibility.Modules;

/// <summary>
/// Shell 暴露给命令工作台模块的挂载点：共享选择态、目录会话接到控制台，以及补全路由。
/// </summary>
public interface IShellCommandWorkbenchHost
{
    /// <summary>宿主指令总线。</summary>
    CommandBus Bus { get; }

    /// <summary>命令集与详情联动选中态。</summary>
    CommandSelectionState CommandSelection { get; }

    /// <summary>设置存储。</summary>
    ISettingsService Settings { get; }

    /// <summary>宿主日志。</summary>
    IShellLog Log { get; }

    /// <summary>数据根目录。</summary>
    string DataDirectory { get; }

    /// <summary>把真实目录会话挂到控制台延迟代理上。</summary>
    void AttachCommandCatalogSession(ICommandCatalogSession session);

    /// <summary>配置控制台补全聚焦判定与非聚焦时唤起命令集。</summary>
    void ConfigureCommandCompletionRouting(Func<bool> isConsoleFocused, Action showCommandCatalog);

    /// <summary>布局变化后刷新补全聚焦态。</summary>
    void RefreshCommandCompletionFocus();
}

/// <summary>模块实现本接口后，宿主在 CreateUi 前注入命令工作台挂载点。</summary>
public interface IShellCommandWorkbenchAware
{
    /// <summary>命令工作台宿主；服务宿主或未装配时可为 null。</summary>
    IShellCommandWorkbenchHost? CommandWorkbench { set; }
}

/// <summary>
/// 由模块**反向提供**命令工作台挂载点。
///
/// 与 <see cref="IShellCommandWorkbenchAware"/> 方向相反，和
/// <c>IShellUiProvider</c> 完全对称：界面整体成为模块之后（Aurora DEC-008），
/// 宿主自己不再持有任何界面实现，挂载点只能来自承载界面的那个模块。
///
/// 少了这条反向通道，<see cref="IShellCommandWorkbenchAware.CommandWorkbench"/>
/// 永远是 null——症状是命令集与指令详情两个页面一声不响地不出现，
/// 控制台的补全同时退化到本地注册表兜底，而日志里没有任何错误。
/// </summary>
public interface IShellCommandWorkbenchProvider
{
    /// <summary>本模块提供的命令工作台挂载点；界面尚未就绪时为 null。</summary>
    IShellCommandWorkbenchHost? CommandWorkbench { get; }
}
