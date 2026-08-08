using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Core.Mcp;

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
