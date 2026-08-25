using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 模块开发路线的指令面：从主分支拉出工作区，到改完合并回去、发布、装机。
/// </summary>
/// <remarks>
/// **为什么这条路线住在宿主里。**
///
/// 它此前住在 HistoryDiana。那样的话，每一轮模块开发都依赖 Diana 装载成功——
/// 而 Diana 自己也是模块，也要走这条路线来改。一旦 Diana 坏了，修复就退化成
/// 直接改主分支救急，整条路线跟着塌掉。
///
/// 放在宿主则相反：宿主坏了，所有模块本来就跑不起来，开发本来就该谨慎且少量；
/// 而只要宿主活着，**任何一个模块坏掉都能用这条路线单独修好**，
/// 包括承载对外传输的 HistoryPortunus 和承载界面的 HistoryAurora。
///
/// 这也是整条路线都在命令行面上的原因（见 <c>CliExposurePolicy.ExposedCommands</c>）：
/// 走 MCP 要 agent 会话活着，走 Web 要 Portunus 装载成功，而需要修模块的时刻
/// 恰恰是这些前提不成立的时刻。<c>--cli</c> 在本进程内执行，一样都不需要。
///
/// 模块公开面是三条：<c>vulcan.dev.start</c> / <c>submit</c> / <c>finish</c>，
/// <strong>不配 MCP</strong>，只走同目录 Console <c>HistoryVulcan.Cli.exe --cli</c>；GUI EXE 入口仅作兼容。
/// 工作区（<see cref="WorktreeCommands"/>）与发布（<see cref="ReleaseCommands"/>）仍注册，但对 MCP 隐藏；
/// 宿主打包走 <c>--cli vulcan.release.cycle</c>。
/// </remarks>
public static class DevelopmentCommands
{
    /// <summary>把开发路线的全部指令注册进宿主注册表。</summary>
    /// <param name="registry">宿主注册表。</param>
    /// <param name="bus">权威指令总线；装包经它走，与模块管理页的热重载按钮同一条路径。</param>
    /// <param name="settings">宿主设置；项目库根与工作区根可配。</param>
    /// <param name="dataDirectory">应用数据根；发布日志以它为基准。</param>
    public static void RegisterAll(
        CommandRegistry registry,
        CommandBus bus,
        ISettingsService settings,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var context = new DevelopmentContext(bus, settings, dataDirectory);
        WorktreeCommands.Register(registry, context);
        ReleaseCommands.Register(registry, context);
        DevPipelineCommands.Register(registry, context);
    }
}
