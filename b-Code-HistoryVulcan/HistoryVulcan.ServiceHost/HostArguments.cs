namespace HistoryVulcan.ServiceHost;

/// <summary>宿主进程被要求做的事。</summary>
public enum HostAction
{
    /// <summary>启动后台服务（无参数时的缺省）。</summary>
    RunService,

    /// <summary>无头导出命令手册。</summary>
    ExportManual,

    /// <summary>修复登录自启动项。</summary>
    RepairAutostart,

    /// <summary>离线安装模块包。</summary>
    InstallModule,

    /// <summary>执行一条已声明命令行暴露的指令。</summary>
    RunCommand,

    /// <summary>参数无法识别或缺少必需值。</summary>
    Error,

    /// <summary>打印 CLI 帮助。</summary>
    Help,

    /// <summary>打印宿主版本。</summary>
    Version,

    /// <summary>连接已经运行的宿主执行受限运行时指令。</summary>
    RunRuntime,

}

/// <summary>
/// 入口参数的判定结果。
/// </summary>
/// <param name="Action">要执行的动作。</param>
/// <param name="Value">动作的参数：路径或指令文本；<see cref="HostAction.RunService"/> 时为空。</param>
/// <param name="Error">仅 <see cref="HostAction.Error"/> 时有值。</param>
public readonly record struct HostArguments(HostAction Action, string Value, string Error)
{
    /// <summary>结果格式；默认是人类可读文本。</summary>
    public HostOutputFormat Format { get; init; } = HostOutputFormat.Human;

    /// <summary>是否为运行时动作提供一次性本地批准。</summary>
    public bool Approve { get; init; }
}

/// <summary>CLI 输出格式。</summary>
public enum HostOutputFormat
{
    Human,
    Json,
}

/// <summary>
/// 入口参数判定。抽成纯函数是为了能被测试——理由见 <see cref="Parse"/>。
/// </summary>
public static class HostArgumentParser
{
    /// <summary>无头导出命令手册。</summary>
    public const string ExportManualSwitch = "--export-command-manual";

    /// <summary>修复登录自启动项。</summary>
    public const string RepairAutostartSwitch = "--repair-autostart";

    /// <summary>离线安装模块包。</summary>
    public const string InstallModuleSwitch = "--install-module";

    /// <summary>执行一条指令。</summary>
    public const string CommandLineSwitch = "--cli";

    /// <summary>连接正在运行的宿主。</summary>
    public const string RuntimeSwitch = "--runtime";

    /// <summary>打印帮助。</summary>
    public const string HelpSwitch = "--help";

    /// <summary>打印版本。</summary>
    public const string VersionSwitch = "--version";

    /// <summary>选择输出格式。</summary>
    public const string FormatSwitch = "--format";

    /// <summary>批准一次运行时动作。</summary>
    public const string ApproveSwitch = "--approve";

    /// <summary>
    /// 双角色时代留下的兼容开关，识别并忽略。
    ///
    /// 自启动项、既有快捷方式和 <c>vulcan.svc.restart</c> 都还带着它；
    /// 对它报错只会让升级过程平白失败。
    /// </summary>
    public const string LegacyServiceSwitch = "--service";

    /// <summary>
    /// 判定入口参数。
    /// </summary>
    /// <remarks>
    /// **不认识的参数一律 <see cref="HostAction.Error"/>，绝不落到起服务那条缺省路径上。**
    ///
    /// 此前的形状是静默忽略：<c>HistoryVulcan.exe --instal-module pkg</c>（少一个 l）
    /// 会默默起一个新宿主，而使用者以为自己装了个包——两个宿主抢同一个端口，
    /// 症状出现在几分钟之后、别的地方。加子命令层让这个坑变深，开关越多打错的机会越多，
    /// 所以判定必须先于任何副作用发生，并且能被单独测到。
    /// </remarks>
    public static HostArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var format = ReadFormat(args, out var formatError);
        if (formatError != null)
            return new HostArguments(HostAction.Error, "", formatError);
        var resolvedFormat = format ?? HostOutputFormat.Human;

        var meta = IndexOf(args, HelpSwitch) >= 0 ? HelpSwitch :
            IndexOf(args, VersionSwitch) >= 0 ? VersionSwitch : null;
        if (meta != null)
        {
            var metaUnknown = args
                .Select((argument, index) => (argument, index))
                .Where(item => !item.argument.Equals(meta, StringComparison.OrdinalIgnoreCase)
                    && !item.argument.Equals(ApproveSwitch, StringComparison.OrdinalIgnoreCase)
                    && !item.argument.Equals(FormatSwitch, StringComparison.OrdinalIgnoreCase)
                    && (item.index == 0 || !args[item.index - 1].Equals(FormatSwitch, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.argument)
                .ToArray();
            if (metaUnknown.Length > 0)
                return new HostArguments(HostAction.Error, "", $"无法识别的参数: {string.Join(' ', metaUnknown)}") { Format = resolvedFormat };
            return new HostArguments(meta == HelpSwitch ? HostAction.Help : HostAction.Version, "", "")
            { Format = resolvedFormat };
        }

        var approve = IndexOf(args, ApproveSwitch) >= 0;

        // 顺序即优先级：取值型开关先判，因为它们的「缺值」也要报错而不是退化成起服务。
        foreach (var name in new[] { ExportManualSwitch, InstallModuleSwitch })
        {
            var index = IndexOf(args, name);
            if (index < 0)
                continue;
            if (index + 1 >= args.Length)
                return new HostArguments(HostAction.Error, "", $"{name} 需要一个路径参数。") { Format = resolvedFormat };

            var action = name == ExportManualSwitch ? HostAction.ExportManual : HostAction.InstallModule;
            return new HostArguments(action, args[index + 1], "") { Format = resolvedFormat };
        }

        var runtime = IndexOf(args, RuntimeSwitch);
        if (runtime >= 0)
        {
            var text = JoinCommandArguments(args, runtime + 1);
            return text.Length == 0
                ? new HostArguments(HostAction.Error, "", $"{RuntimeSwitch} 需要一条指令文本。") { Format = resolvedFormat }
                : new HostArguments(HostAction.RunRuntime, text, "") { Format = resolvedFormat, Approve = approve };
        }

        // --cli 吃掉其后的全部参数：指令带参数是常态，逐个引号转义只会让人在 shell 里踩坑。
        var cli = IndexOf(args, CommandLineSwitch);
        if (cli >= 0)
        {
            var text = JoinCommandArguments(args, cli + 1);
            return text.Length == 0
                ? new HostArguments(HostAction.Error, "", $"{CommandLineSwitch} 需要一条指令文本。") { Format = resolvedFormat }
                : new HostArguments(HostAction.RunCommand, text, "") { Format = resolvedFormat };
        }

        if (IndexOf(args, RepairAutostartSwitch) >= 0)
            return new HostArguments(HostAction.RepairAutostart, "", "");

        var unknown = args
            .Where(argument => !argument.Equals(LegacyServiceSwitch, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return unknown.Length > 0
            ? new HostArguments(HostAction.Error, "", $"无法识别的参数: {string.Join(' ', unknown)}") { Format = resolvedFormat }
            : new HostArguments(HostAction.RunService, "", "");
    }

    /// <summary>用法说明，供 <see cref="HostAction.Error"/> 时打印。</summary>
    public static IReadOnlyList<string> UsageLines { get; } =
    [
        "用法:",
        "  HistoryVulcan.exe                                 启动后台服务",
        $"  HistoryVulcan.exe {CommandLineSwitch} <指令 [参数...]>     执行一条已声明暴露的指令",
        $"  HistoryVulcan.Cli.exe {RuntimeSwitch} <指令 [参数...]>     连接运行中的宿主",
        $"  HistoryVulcan.Cli.exe {HelpSwitch} / {VersionSwitch}       查看 CLI 合同",
        $"  ... {FormatSwitch} json                              输出单一 JSON 结果",
        $"  ... {ApproveSwitch}                                  批准运行时动作",
        $"  HistoryVulcan.exe {InstallModuleSwitch} <包目录>    离线安装模块包（宿主起不来时用）",
        $"  HistoryVulcan.exe {ExportManualSwitch} <路径>",
        $"  HistoryVulcan.exe {RepairAutostartSwitch}",
    ];

    private static int IndexOf(string[] args, string name)
        => Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string JoinCommandArguments(string[] args, int start)
        => string.Join(' ', args.Skip(start)
            .Where((argument, index) =>
                !argument.Equals(ApproveSwitch, StringComparison.OrdinalIgnoreCase)
                && !argument.Equals(FormatSwitch, StringComparison.OrdinalIgnoreCase)
                && !(index > 0 && args[start + index - 1].Equals(FormatSwitch, StringComparison.OrdinalIgnoreCase))))
            .Trim();

    private static HostOutputFormat ReadFormat(string[] args)
        => ReadFormat(args, out _) ?? HostOutputFormat.Human;

    private static HostOutputFormat? ReadFormat(string[] args, out string? error)
    {
        error = null;
        var index = IndexOf(args, FormatSwitch);
        if (index < 0)
            return HostOutputFormat.Human;
        if (index + 1 >= args.Length)
        {
            error = $"{FormatSwitch} 需要 human 或 json。";
            return null;
        }

        return args[index + 1].ToLowerInvariant() switch
        {
            "human" => HostOutputFormat.Human,
            "json" => HostOutputFormat.Json,
            _ => SetFormatError($"{FormatSwitch} 只支持 human 或 json。", out error),
        };
    }

    private static HostOutputFormat? SetFormatError(string message, out string? error)
    {
        error = message;
        return null;
    }
}
