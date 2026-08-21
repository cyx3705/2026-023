using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 宿主自带的确认服务：一律拒绝。
///
/// 确认的语义是「请人过目」，而宿主进程里没有人可问——它是个后台服务。
/// 真正能弹框的是承载界面的那个模块：Aurora 在装载时把 <c>Bus.Confirmation</c>
/// 换成自己的窗口确认（见 <c>AuroraShellHost</c>），本实现随即不再被调用。
///
/// 4.2.0 之前这里会把确认经 WebSocket 中继给进程外前端。那条路随 DEC-008 的
/// 独立 exe 一起退役了——中继在没有连接时同样是拒绝，所以行为没有变化，
/// 变的只是不再假装存在一条通道。
///
/// **拒绝而不是放行**：放行会让「危险命令需确认」整条约束失效。
/// 每次拒绝写一条 warn，否则用户只会看到命令莫名失败。
/// </summary>
public sealed class ShellRelayConfirmation : IConfirmationService
{
    private readonly IShellLog _log;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellRelayConfirmation(IShellLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Confirm(string prompt) => Refuse(prompt, client: null);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool? ConfirmRemote(string client, string prompt, int timeoutSeconds)
        => Refuse(prompt, client);

    private bool Refuse(string prompt, string? client)
    {
        _log.Warn("confirm", $"界面未装载，确认请求已拒绝{Describe(client)}: {prompt}");
        return false;
    }

    private static string Describe(string? client)
        => string.IsNullOrWhiteSpace(client) ? "" : $"(来源 {client})";
}
