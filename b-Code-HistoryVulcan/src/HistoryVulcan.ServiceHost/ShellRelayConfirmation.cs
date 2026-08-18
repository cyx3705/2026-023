using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Services.Web;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 把人工确认中继到前端的确认通道（4.0.0，REQ-A2）。
///
/// 取代此前的 <c>ServiceConfirmation</c>——那个实现在服务进程里直接弹
/// <c>MessageBox</c>，是服务侧最后一处业务性 WPF 依赖，也让"无头服务"名不副实：
/// 一个没有界面的后台进程弹出模态框，在无人值守场景下会一直阻塞到超时。
///
/// **没有前端连接时一律拒绝。** 这不是保守选择而是唯一正确的选择：确认的语义是
/// "请人过目"，没有人可问就等于没得到批准。放行会让 MCP 侧"危险命令需确认"整条约束失效。
/// 拒绝时写一条 warn 日志，否则用户只会看到命令莫名失败。
/// </summary>
public sealed class ShellRelayConfirmation : IConfirmationService
{
    private readonly Func<WebGateway?> _gateway;
    private readonly IShellLog _log;
    private readonly TimeSpan _timeout;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellRelayConfirmation(Func<WebGateway?> gateway, IShellLog log, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(log);
        _gateway = gateway;
        _log = log;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Confirm(string prompt) => Ask(prompt, _timeout, client: null);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool? ConfirmRemote(string client, string prompt, int timeoutSeconds)
        => Ask(
            prompt,
            timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : _timeout,
            client);

    private bool Ask(string prompt, TimeSpan timeout, string? client)
    {
        var gateway = _gateway();
        if (gateway == null || !gateway.IsRunning)
        {
            _log.Warn("confirm", $"网关未运行，确认请求已拒绝{Describe(client)}: {prompt}");
            return false;
        }

        if (gateway.ConnectedShells == 0)
        {
            _log.Warn("confirm", $"前端未连接，确认请求已拒绝{Describe(client)}: {prompt}");
            return false;
        }

        var approved = gateway.RequestShellConfirmation(prompt, timeout);
        if (!approved)
            _log.Info("confirm", $"确认被拒绝或超时{Describe(client)}: {prompt}");
        return approved;
    }

    private static string Describe(string? client)
        => string.IsNullOrWhiteSpace(client) ? "" : $"(来源 {client})";
}
