using HistoryVulcan.Core.Commands;

using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Extensibility.Commands;

/// <summary>
/// 域聚焦下的输入解析（DEC-025 / REQ-CMD-012）。纯函数，不持有注册表也不碰界面，
/// 控制台与 Mercury 补全引擎共用同一份判定，避免两侧各写一套导致行为漂移。
/// </summary>
/// <remarks>
/// 只有两条规则：输入首段命中**已注册域**时按绝对名解析；否则补上当前聚焦域前缀。
/// 「退出聚焦」因此不需要任何域提供指令——<c>mercury.go</c>、<c>vulcan.*</c>
/// 的首段本身就是已注册域，在任何聚焦状态下都能直接输入。
/// </remarks>
public static class DomainFocus
{
    /// <summary>域筛选下拉里代表「不聚焦」的值。</summary>
    public const string All = "全部";

    /// <summary>该值是否表示未聚焦到任何域。</summary>
    public static bool IsUnfocused(string? domain)
        => string.IsNullOrWhiteSpace(domain) || domain.Trim() == All;

    /// <summary>
    /// 把用户输入解析成实际要执行的指令文本。
    /// </summary>
    /// <param name="input">用户在控制台输入的原文。</param>
    /// <param name="focusedDomain">当前聚焦域；<see cref="All"/> 或空表示未聚焦。</param>
    /// <param name="isRegisteredDomain">
    /// 判断一个首段是否是已注册域。权威源必须是运行期注册表
    /// （<see cref="CommandRegistry.IsRegisteredDomain"/>），不得传入硬编码域清单。
    /// </param>
    public static string Resolve(string? input, string? focusedDomain, Func<string, bool> isRegisteredDomain)
    {
        ArgumentNullException.ThrowIfNull(isRegisteredDomain);

        var text = (input ?? string.Empty).TrimStart();
        if (text.Length == 0 || IsUnfocused(focusedDomain))
            return input ?? string.Empty;

        var head = HeadSegment(text);
        if (head.Length == 0 || isRegisteredDomain(head))
            return input ?? string.Empty;

        return $"{focusedDomain!.Trim()}.{text}";
    }

    /// <summary>
    /// 输入是否会被拼上聚焦域前缀。界面据此提示「当前输入将解析为 &lt;域&gt;.…」。
    /// </summary>
    public static bool WouldPrefix(string? input, string? focusedDomain, Func<string, bool> isRegisteredDomain)
    {
        ArgumentNullException.ThrowIfNull(isRegisteredDomain);

        var text = (input ?? string.Empty).TrimStart();
        if (text.Length == 0 || IsUnfocused(focusedDomain))
            return false;

        var head = HeadSegment(text);
        return head.Length > 0 && !isRegisteredDomain(head);
    }

    /// <summary>
    /// 取第一个 token 的首段（第一个点之前的部分）。没有点时整个 token 就是首段。
    /// </summary>
    private static string HeadSegment(string text)
    {
        var end = text.Length;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '.')
            {
                end = i;
                break;
            }
        }

        return text[..end];
    }
}
