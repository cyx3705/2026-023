// MCP 契约：宿主**声明**的那一半——配置键、审计出口、治理视图。
//
// 与同目录的 McpPolicy.cs 分工：那边是宿主**决定**什么（谁能被远端调用、
// 确认怎么算、提示词是否被篡改），这边是宿主约定「东西长什么样、放在哪」。
//
// 网关本身自 4.4.0 起住在 HistoryPortunus。留在这里的都是**锁**：
// 门可以搬走，决定谁能进门的规则不能跟着门走，否则换一个模块就能给自己放权。

using HistoryVulcan.Core.Clients;
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

/// <summary>
/// MCP 调用留痕（铁律 2 / MS-05：每次调用与拒绝都必须留痕）。
///
/// 0.4.4 抽出：网关此前直接依赖派生应用的 HistoryRecorder，而后者同时承载
/// 应用专有的业务留痕表；把网关真正需要的那一面抽成接口，框架层不再认识应用侧类型。
/// 派生应用若已有自己的留痕器，实现本接口接进来即可复用同一张表。
/// </summary>
public interface IMcpAuditLog
{
    /// <summary>记录一次 MCP 调用结果。</summary>
    /// <param name="client">客户端标识。</param>
    /// <param name="tool">工具名；鉴权阶段的拒绝用 "(auth)"。</param>
    /// <param name="arguments">已脱敏的调用参数文本；持久化实现可进一步截断。</param>
    /// <param name="result">结果：成功 / 拒绝 / 远程拒绝 / 确认超时 等。</param>
    /// <param name="elapsedMs">耗时毫秒；未执行记 0。</param>
    void RecordMcp(string client, string tool, string arguments, string result, long elapsedMs);

    /// <summary>记录带稳定会话身份的 MCP 调用；旧实现自动转发到兼容签名。</summary>
    void RecordMcp(ClientSession session, string tool, string arguments, string result, long elapsedMs)
        => RecordMcp(session.Name, tool, arguments, result, elapsedMs);
}

/// <summary>
/// 提示词治理的只读视图。宿主消费，模块提供。
/// </summary>
/// <remarks>
/// 承接 <c>IEffectivePromptDescriptionReader</c> 早就写明的分工——
/// 「治理的写方刻意在宿主之外，宿主只消费这个只读视图」。那条注释写于治理还在宿主里的时候，
/// 4.4.0 把 <c>PromptGovernanceStore</c> 迁往 HistoryPortunus 之后，它才真正成立。
///
/// **返回值刻意全部退化为基本类型。** 调用方（<c>vulcan.command.list/show</c>）只需要
/// 「改过没有、第几版、几条待审、几起事故」这四格，不需要提案与事故的完整结构。
/// 让治理的记录类型跨过这个边界，等于把宿主重新绑回模块的数据模型上——
/// 那正是这次迁移要解开的东西。
///
/// 实现方随模块热重载来去，因此消费方必须把它当作**随时可能为 null**：
/// 模块没装上、正在重载、或装载失败时，目录指令要照常可用，只是少了治理那几列。
/// </remarks>
public interface IMcpPromptGovernanceView
{
    /// <summary>当前生效的工具描述覆盖，按指令名索引；未被覆盖的指令不出现在结果中。</summary>
    IReadOnlyDictionary<string, string> EffectiveDescriptions();

    /// <summary>各指令待审提案数，按指令名索引。</summary>
    IReadOnlyDictionary<string, int> OpenProposalCounts();

    /// <summary>各指令已记录事故数，按指令名索引。</summary>
    IReadOnlyDictionary<string, int> IncidentCounts();

    /// <summary>指定指令当前生效的描述修订号；未被覆盖时为 null。</summary>
    string? CurrentRevisionId(string commandName);
}
