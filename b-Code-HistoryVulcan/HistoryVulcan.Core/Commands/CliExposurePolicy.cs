namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 决定一条指令能否经命令行入口执行。
/// </summary>
/// <remarks>
/// **命令行是唯一不由描述符声明的消费面，这是刻意的。**
///
/// 暴露与否，描述符上只有一个声明（<see cref="CommandDescriptor.HiddenReason"/>），
/// 三个消费面共用。但命令行有一条别的面没有的额外要求：
/// <c>vulcan.module.install</c> 与 <c>vulcan.module.remove</c> 对 MCP 是**硬排除**
/// （不能让远端给自己换宿主的包），对命令行却是**必须有**
/// （模块坏掉时，<c>--cli</c> 是唯一还能换包的路）。
/// 一个布尔位给不出「这个面要、那个面不要」这个答案。
///
/// 所以命令行的收窄留在命令行自己这里，而不是回到「每个面各发一个令牌」的老路上。
///
/// **名单有代价，必须承认：** 指令改名时它会静默失配，本体系已经因为按名字写的
/// 安全规则栽过四次。补偿措施是 <see cref="MissingCommands"/>——命令行入口每次执行前
/// 拿本名单与真实注册表对账，缺一条就整体报错，改名后第一次用 <c>--cli</c> 就会炸出来，
/// 而不是等到某天需要救火时才发现那条恢复指令不见了。
/// </remarks>
internal static class CliExposurePolicy
{
    /// <summary>
    /// 命令行可执行的全部指令。
    /// </summary>
    /// <remarks>
    /// 恰好是「查目录 + 换包装载 + 模块三步 + 宿主打包/工作区」这几组，不多一条。
    ///
    /// 走 MCP 要 agent 会话活着，走 Web 要 HistoryPortunus 装载成功，
    /// 而需要修模块的时刻恰恰是这些前提不成立的时刻；<c>--cli</c> 在本进程内执行，
    /// 一样都不需要。这条理由不适用于任何别的指令，所以名单也就到此为止。
    /// </remarks>
    public static IReadOnlyList<string> ExposedCommands { get; } =
    [
        // CLI 自发现：只描述这张名单，不绕过名单执行其它总线指令
        "vulcan.cli.list",
        "vulcan.cli.show",

        // 查目录：动手之前先看得见现状
        "vulcan.command.domains",
        "vulcan.command.list",
        "vulcan.command.show",

        // 换包与装载：恢复路径本身
        "vulcan.module.install",
        "vulcan.module.list",
        // 装载是否已经完整。5.1.3 加了这条查询却漏了本名单，于是只有 GUI 控制台和 MCP
        // 问得到——而「模块没装齐」恰恰是 GUI 可能起不来、MCP 可能没装载的那种时刻。
        "vulcan.module.ready",
        "vulcan.module.reload",
        "vulcan.module.remove",
        "vulcan.module.uninstall",
        "vulcan.module.unload",

        // 模块开发三步：只走 --cli，不配 MCP
        "vulcan.dev.start",
        "vulcan.dev.submit",
        "vulcan.dev.finish",

        // 宿主打包；模块不得走本条
        "vulcan.release.cycle",

        // 宿主工作区（模块请用 vulcan.dev.*）
        "vulcan.worktree.create",
        "vulcan.worktree.list",
        "vulcan.worktree.merge",
        "vulcan.worktree.root",
    ];

    private static readonly HashSet<string> Exposed =
        new(ExposedCommands, StringComparer.OrdinalIgnoreCase);

    /// <summary>指定指令是否在命令行面上。</summary>
    public static bool IsExposed(string commandName)
        => !string.IsNullOrWhiteSpace(commandName) && Exposed.Contains(commandName.Trim());

    /// <summary>
    /// 名单里已经不存在于注册表的指令；空表示对得上。
    /// </summary>
    /// <remarks>
    /// 名单按名字写，改名会让它静默失配——而失配的方向最坏：
    /// 需要救火时才发现恢复指令不在面上。调用方应在执行前对账并直接报错。
    /// </remarks>
    public static IReadOnlyList<string> MissingCommands(CommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return ExposedCommands.Where(name => !registry.TryGet(name, out _)).ToList();
    }

    /// <summary>
    /// 不在名单上时给调用方的拒绝说明。
    /// </summary>
    /// <remarks>
    /// 说明里要写清「不是这条指令不存在」，否则使用者会以为自己打错了名字，
    /// 转而去猜别的写法——而真正需要做的是判断它该不该进这份名单。
    /// </remarks>
    public static string RefusalReason(string commandName)
        => $"指令 {commandName} 不在命令行面上。"
           + $"命令行只开放开发管线、模块恢复和 CLI 自发现共 {ExposedCommands.Count} 条，"
           + "见 CliExposurePolicy.ExposedCommands；请使用 --help、vulcan.cli.list，或运行宿主时使用 HistoryVulcan.Cli.exe --runtime。";
}
