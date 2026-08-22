// MCP 策略：宿主**决定**的那一半。
//
// McpExposurePolicy —— 哪些指令可以被远端调用。判据现在是描述符上的 HiddenReason：
//   按名字写的排除失效过四次（debug.logflood、vulcan.log.flood、vulcan.mcp.*），
//   声明跟着指令走才不会在改名时掉队。细节见 HardExclusionReason 注释。
// McpConfirmationScope —— 远端预批准的确认在什么范围内有效。
// PromptTextIntegrity —— 工具描述是否被篡改。
//
// 与同目录 McpContracts.cs 的分工：那边是宿主声明的形状，这边是宿主做的判断。
// 网关搬去了 HistoryPortunus，这三样刻意留下——门搬走，锁留下。

using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Core.Mcp;

/// <summary>
/// MCP 暴露规则的解释器，供网关、command.* 和管理页共同使用。
///
/// **每条判据的单一真值都在描述符上**：只读看 <see cref="CommandDescriptor.Readonly"/>，
/// 问不问看 <see cref="CommandDescriptor.Level"/>，暴不暴露看
/// <see cref="CommandDescriptor.HiddenReason"/>。模块级的暴露档来自模块清单的
/// <c>mcpExposure</c>（经 <see cref="ModuleExposure"/> 查得）。
/// 本类只负责把这些来源解释成最终档位，**自身不再持有任何指令名清单**（4.8.0）。
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
    /// 模块清单声明 <c>mcpExposure=readonly</c> 时，该模块的指令按只读对待。
    /// </summary>
    /// <remarks>
    /// 4.8.0 之前这里还有一份「按名字补登记只读」的字典与配套的 <c>RegisterReadonly</c>。
    /// 它自 V2.4.4 起就恒为空——只读性的单一真值早已是
    /// <see cref="CommandDescriptor.Readonly"/>——全仓唯一的调用方是它自己的测试。
    /// 一个只被自己的测试调用的兜底通道不是兜底，是还没被删掉。
    /// </remarks>
    public static bool IsReadonlyAllowed(string commandName)
        => string.Equals(ExposureOf(commandName), "readonly", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 一条指令不对远端暴露的原因；null 表示暴露。
    /// </summary>
    /// <remarks>
    /// **判据挂在描述符上，不再是一份按名字写的清单。**
    ///
    /// 原先这里有一长串 <c>commandName.Equals(...)</c> / <c>StartsWith("debug.")</c> /
    /// <c>EndsWith(".ui.data")</c>。那份清单失效过四次，每次都是同一个原因：
    /// 指令改了名，规则还盯着旧名字——<c>debug.logflood</c> 收编为 <c>vulcan.log.flood</c>
    /// 之后承压注水指令可被远程触发；<c>vulcan.mcp.*</c> 随网关迁往 HistoryPortunus
    /// 改名之后，start/stop/autostart 一并变成远端可见工具。
    ///
    /// 现在原因写在 <see cref="CommandDescriptor.HiddenReason"/> 上，跟着指令一起改名、
    /// 一起搬家。清单里那两条守着**全仓不存在的指令**
    /// （<c>vulcan.log.flood</c>、<c>vulcan.svc.forgetfrontend</c>）也随之消失。
    ///
    /// 模块清单的 <c>mcpExposure=hidden</c> 是另一条通道，保留：那是模块作者在包一级
    /// 做的声明，不需要改到每条描述符。
    /// </remarks>
    public static string? HardExclusionReason(CommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.HiddenReason is { Length: > 0 } reason)
            return reason;
        return string.Equals(ExposureOf(descriptor.Name), "hidden", StringComparison.OrdinalIgnoreCase)
            ? "模块清单声明 mcpExposure=hidden,不对 MCP 暴露(Q211-2)"
            : null;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string State(CommandDescriptor descriptor)
    {
        if (HardExclusionReason(descriptor) != null)
            return "hidden";
        if (descriptor.Level == CommandLevel.Ask)
            return "dangerous";
        if (descriptor.Readonly)
            return "readonly";
        return IsReadonlyAllowed(descriptor.Name) ? "readonly" : "standard";
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool IsVisible(CommandDescriptor descriptor, string policy)
        => HardExclusionReason(descriptor) == null
           && descriptor.Level != CommandLevel.Ask
           && (policy.Equals("standard", StringComparison.OrdinalIgnoreCase)
               || descriptor.Readonly
               || IsReadonlyAllowed(descriptor.Name));
}

/// <summary>
/// MCP 确认中继的执行域(V2.2 CX-02/03)。
/// 网关在宿主端弹框获得人工批准后,用本域标记"这次总线执行的二次确认已由人工完成",
/// 使总线不再重复弹框;AsyncLocal 只沿网关发起的异步流前向传播,UI/手动/脚本指令的
/// 确认路径不受影响(其 AsyncLocal 恒为 false)。
/// </summary>
public static class McpConfirmationScope
{
    private static readonly AsyncLocal<bool> _preApproved = new();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool PreApproved => _preApproved.Value;

    /// <summary>在预批准标记下执行网关发起的总线调用;结束后复位。</summary>
    public static async Task<T> RunPreApprovedAsync<T>(Func<Task<T>> action)
    {
        _preApproved.Value = true;
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _preApproved.Value = false;
        }
    }
}

/// <summary>
/// 包装 Shell 交互确认(或 --yes 的自动确认)的确认服务(V2.2 CX-03):
/// MCP 中继已由人工预批准的执行直接放行(不二次弹框);其余一律走内层——
/// 即 UI/手动的真实弹框,或 --yes 的自动确认。
/// 关键:MCP 危险调用的确认由网关独立完成,从不经过 --yes 的自动确认,
/// 因此 --yes 对 MCP 来源危险指令不生效(CX-03)。
/// </summary>
public sealed class GatewayAwareConfirmation : IConfirmationService
{
    private readonly IConfirmationService? _inner;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public GatewayAwareConfirmation(IConfirmationService? inner) => _inner = inner;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Confirm(string prompt)
        // 无内层服务时安全缺省拒绝,与总线"无确认通道即拒绝"一致
        => McpConfirmationScope.PreApproved || (_inner?.Confirm(prompt) ?? false);
}

/// <summary>拒绝已在上游丢失、无法由 UTF-8 解码恢复的提示词文本。</summary>
public static class PromptTextIntegrity
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string ValidateDescription(string text)
    {
        text = text.Trim();
        if (text.Length == 0)
            throw new InvalidOperationException("描述不能为空");
        if (text.Length > 2000)
            throw new InvalidOperationException($"描述过长({text.Length} 字符，上限 2000)");
        if (LooksCorrupted(text))
            throw new InvalidOperationException(
                "描述疑似发生编码损坏（包含替换字符或大量连续问号），已拒绝保存；" +
                "请使用 UTF-8 JSON 请求体重新提交");
        return text;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool LooksCorrupted(string text)
    {
        if (text.Contains('\uFFFD'))
            return true;

        var questionMarks = 0;
        var consecutive = 0;
        foreach (var character in text)
        {
            if (character == '?')
            {
                questionMarks++;
                consecutive++;
                if (consecutive >= 3)
                    return true;
            }
            else
            {
                consecutive = 0;
            }
        }

        return questionMarks >= 4 && questionMarks * 4 >= text.Length;
    }
}
