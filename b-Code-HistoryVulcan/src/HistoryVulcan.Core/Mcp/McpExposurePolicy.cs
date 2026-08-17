using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Core.Mcp;

/// <summary>
/// MCP 暴露规则的解释器，供网关、command.* 和管理页共同使用。
///
/// **只读性的单一真值在 <see cref="CommandDescriptor.Readonly"/>**（命令自己声明）；
/// 模块命令的暴露档来自模块清单的 <c>mcpExposure</c>（经 <see cref="ModuleExposure"/> 查得）。
/// 本类只负责把这两个来源解释成最终档位，不再持有任何指令名清单（V2.4.4）。
/// </summary>
public static class McpExposurePolicy
{
    private static Func<string, string?>? _moduleOfCommand;
    private static Func<string, string?>? _moduleExposure;

    /// <summary>V2.2 CX-01:命令名 → 所属模块名(注册来源 "module:&lt;name&gt;");装配点接 Registry.GetSource。</summary>
    public static Func<string, string?>? ModuleOfCommand
    {
        get => Volatile.Read(ref _moduleOfCommand);
        set => Volatile.Write(ref _moduleOfCommand, value);
    }

    /// <summary>V2.2 CX-01:模块名 → 清单声明的 mcpExposure;无清单(根平铺)返回 null = standard 现状。</summary>
    public static Func<string, string?>? ModuleExposure
    {
        get => Volatile.Read(ref _moduleExposure);
        set => Volatile.Write(ref _moduleExposure, value);
    }

    private static string? ExposureOf(string commandName)
    {
        var module = ModuleOfCommand?.Invoke(commandName);
        return module == null ? null : ModuleExposure?.Invoke(module);
    }

    /// <summary>
    /// 按名字补登记的只读指令集合。
    ///
    /// **V2.4.4 起默认为空**:只读性的单一真值是 <see cref="CommandDescriptor.Readonly"/>——
    /// 由命令在注册处自己声明,与 ConfirmPrompt 同级。
    /// 0.4.4 时代的 18 条框架基线与派生应用登记的 14 条已全部迁至各自描述符,
    /// 保留双份会让「一件事实两处声明」以新形态复活,故本集合清空。
    ///
    /// 本集合与 <see cref="RegisterReadonly"/> 继续保留,仅用于**无法修改注册点**的场景
    /// (例如第三方程序集提供的命令描述符)。正常开发一律用 Readonly = true,不要走这里。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ReadonlyCommands =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按名字补登记只读指令(0.4.4 引入,V2.4.4 起退为兜底通道)。幂等、可重复调用。
    ///
    /// **优先用 <see cref="CommandDescriptor.Readonly"/> 自描述**;只有在拿不到注册点、
    /// 无法给描述符加字段时才用本方法。
    /// </summary>
    public static void RegisterReadonly(params string[] commandNames)
    {
        foreach (var name in commandNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
                ReadonlyCommands.TryAdd(name.Trim(), 0);
        }
    }

    /// <summary>当前生效的只读指令全集(框架基线 + 派生登记),供管理页与自检使用。</summary>
    public static IReadOnlyCollection<string> ReadonlyCommandNames => ReadonlyCommands.Keys.ToArray();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool IsReadonlyAllowed(string commandName)
        => ReadonlyCommands.ContainsKey(commandName)
           || string.Equals(ExposureOf(commandName), "readonly", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 收编进正式域但仍属诊断性质的指令:永不对 MCP/Web 暴露。
    /// 影子域 <c>debug</c> 退役后,这类指令不再能靠名称前缀识别(DEC-023)。
    /// </summary>
    private static readonly HashSet<string> DiagnosticCommands =
        new(StringComparer.OrdinalIgnoreCase) { "vulcan.log.flood" };

    /// <summary>诊断指令名单,供自检与文档核对。</summary>
    public static IReadOnlyCollection<string> DiagnosticCommandNames => DiagnosticCommands.ToArray();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string? HardExclusionReason(string commandName)
    {
        if (commandName.Equals("vulcan.module.install", StringComparison.OrdinalIgnoreCase)
            || commandName.Equals("vulcan.module.remove", StringComparison.OrdinalIgnoreCase))
            return "运行包变更只允许认证的本机宿主通道";
        if (commandName.Equals("vulcan.app.quit", StringComparison.OrdinalIgnoreCase))
            return "远程客户端不得退出宿主";
        if (commandName.StartsWith("debug.", StringComparison.OrdinalIgnoreCase)
            || DiagnosticCommands.Contains(commandName))
        {
            // 3.3.2 把 debug.logflood 收编为 vulcan.log.flood(影子域退役,DEC-023)。
            // 该改名一度让这条按 "debug." 前缀生效的硬排除失效——承压注水指令因此可被
            // MCP/Web 远程触发(rate=100000 × seconds=600 即 6000 万条)。前缀规则保留给
            // 未迁移的模块调试指令,收编后的诊断指令改为按名登记。
            return "调试与承压指令不对远程暴露";
        }
        if (commandName.StartsWith("vulcan.mcp.", StringComparison.OrdinalIgnoreCase))
            return "防止远程递归管理或关闭 MCP 服务";
        if (string.Equals(ExposureOf(commandName), "hidden", StringComparison.OrdinalIgnoreCase))
            return "模块清单声明 mcpExposure=hidden,不对 MCP 暴露(Q211-2)";
        return null;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string State(CommandDescriptor descriptor)
    {
        if (HardExclusionReason(descriptor.Name) != null)
            return "hidden";
        if (descriptor.IsDangerous)
            return "dangerous";
        if (descriptor.Readonly)
            return "readonly";
        return IsReadonlyAllowed(descriptor.Name) ? "readonly" : "standard";
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool IsVisible(CommandDescriptor descriptor, string policy)
        => HardExclusionReason(descriptor.Name) == null
           && !descriptor.IsDangerous
           && (descriptor.ExecutionSite != CommandExecutionSite.Frontend
               || descriptor.AllowMcpExecution)
           && (policy.Equals("standard", StringComparison.OrdinalIgnoreCase)
               || descriptor.Readonly
               || IsReadonlyAllowed(descriptor.Name));
}
