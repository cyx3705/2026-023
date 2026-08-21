namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 决定一条指令能否经命令行入口执行。
/// </summary>
/// <remarks>
/// **缺省全关，逐条声明。** 这一点与 <c>McpExposurePolicy</c> 相反，是刻意的：
/// MCP 面对的是一份已经存在、长年累月长起来的指令集，缺省全关等于让它一夜失能，
/// 所以它按「危险/只读/隐藏」推断。CLI 是一张白纸，没有存量要照顾——
/// 新面缺省全开，等于把「还没想过要不要暴露」写成「已经暴露」。
///
/// 判据放在 <see cref="CommandDescriptor.AllowCliExecution"/> 上而不是一张名单里：
/// 指令跟着它的实现走，声明也应该跟着实现走。名单会在指令改名、迁模块时悄悄失配——
/// 本体系已经因为「按名字前缀写的安全排除」栽过三次
/// （<c>debug.logflood</c>、<c>vulcan.log.flood</c>、<c>vulcan.mcp.*</c>）。
///
/// 门在模块里，锁留在宿主：命令行客户端搬到哪都不能给自己放权，
/// 因为它拿到的描述符是宿主注册表里的那一份。
/// </remarks>
public static class CliExposurePolicy
{
    /// <summary>指定指令是否已声明为命令行可执行。</summary>
    public static bool IsExposed(CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.AllowCliExecution;
    }

    /// <summary>
    /// 未声明时给调用方的拒绝说明。
    /// </summary>
    /// <remarks>
    /// 说明里要写清「不是这条指令不存在」，否则使用者会以为自己打错了名字，
    /// 转而去猜别的写法——而真正需要做的是给那条指令加声明。
    /// </remarks>
    public static string RefusalReason(string commandName)
        => $"指令 {commandName} 未声明命令行暴露。"
           + "命令行面缺省全关，需要在指令定义上置 AllowCliExecution=true。";
}
