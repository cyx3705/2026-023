using HistoryVulcan.Core.Clients;

namespace HistoryVulcan.Core.Mcp;

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
