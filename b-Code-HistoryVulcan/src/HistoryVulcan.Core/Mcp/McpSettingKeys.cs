using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Core.Mcp;

/// <summary>
/// MCP 的配置键与策略解析。
/// </summary>
/// <remarks>
/// 这些键此前是 <c>McpGateway</c> 的常量。网关迁往 HistoryPortunus（4.4.0）后它们留在宿主，
/// 因为**配置是契约，网关是实现**：
///
/// - 宿主的 <c>vulcan.app.set/get</c> 按 <c>mcp.*</c> 前缀放行这些键，不问谁来消费；
/// - 旧前端设置的迁移（<c>MigrateLegacyMcpSettings</c>）必须逐个键搬，而它跑在装载模块之前；
/// - <c>vulcan.command.list</c> 要按当前策略判断一条指令对远端是否可见。
///
/// 换句话说：即使 Portunus 没装上，这些键的含义依然成立，只是没有东西在读第二类之外的键。
/// 把它们跟着网关搬走，会让宿主为了知道"用户配了什么策略"而依赖一个模块。
/// </remarks>
public static class McpSettingKeys
{
    /// <summary>监听端口。</summary>
    public const string Port = "mcp.port";

    /// <summary>暴露策略：readonly（默认）/ standard。</summary>
    public const string Policy = "mcp.policy";

    /// <summary>访问令牌。</summary>
    public const string Token = "mcp.token";

    /// <summary>是否随宿主自动监听。</summary>
    public const string Autostart = "mcp.autostart";

    /// <summary>单次调用超时（秒）。</summary>
    public const string Timeout = "mcp.timeout";

    /// <summary>确认中继模式：deny（默认）/ host。</summary>
    public const string Confirm = "mcp.confirm";

    /// <summary>确认等待上限（秒）。</summary>
    public const string ConfirmTimeout = "mcp.confirmtimeout";

    /// <summary>端口被占用时的顺延次数。</summary>
    public const string PortRetries = "mcp.portretries";

    /// <summary>并发会话上限。</summary>
    public const string SessionLimit = "mcp.sessionlimit";

    /// <summary>本设置存储里全部 MCP 配置键，供迁移与巡检按序遍历。</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Port, Policy, Token, Autostart, Timeout, Confirm, ConfirmTimeout, PortRetries, SessionLimit,
    ];

    /// <summary>
    /// 解析当前生效的暴露策略。
    ///
    /// 缺省与无法识别的取值一律落到 <c>readonly</c>——策略解析失败必须收紧而不是放开，
    /// 否则一个拼错的配置值就会把全部 standard 级指令暴露出去。
    /// </summary>
    public static string ResolvePolicy(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var configured = settings.Get(Policy);
        return configured != null && configured.Equals("standard", StringComparison.OrdinalIgnoreCase)
            ? "standard"
            : "readonly";
    }
}
