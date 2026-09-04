namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 模块名 → 指令域的唯一归一化真值（DEC-023）。
/// 模块名保留 <c>History</c> 品牌前缀，指令域去掉它：<c>HistoryJanus</c> → <c>janus</c>。
/// 品牌前缀标识产品族归属，在域段里对所有模块都相同，不携带区分信息。
/// </summary>
public static class ModuleDomainNaming
{
    private const string BrandPrefix = "History";

    /// <summary>
    /// 把模块名归一化为指令域：大小写不敏感地剥离开头的 <c>History</c> 并转小写；
    /// 剥离后为空时退回原名小写；不以 <c>History</c> 开头的原样转小写。
    /// </summary>
    public static string ToDomain(string moduleName)
    {
        var trimmed = moduleName?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return string.Empty;

        if (!trimmed.StartsWith(BrandPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed.ToLowerInvariant();

        var stripped = trimmed[BrandPrefix.Length..].Trim();
        return stripped.Length == 0
            ? trimmed.ToLowerInvariant()
            : stripped.ToLowerInvariant();
    }
}

/// <summary>
/// 「无类」显示标签的唯一真源（DEC-025）。两段名 <c>&lt;域&gt;.&lt;方法&gt;</c> 是该域的
/// 无类直接方法，其类为空串；目录、控制台与命令集都用这里的标签显示与筛选。
/// </summary>
/// <remarks>
/// 这里**只做显示层翻译**，不承担任何类推导。类推导的唯一入口是
/// <c>CommandRegistry.LegacyClass</c>；3.3.2 曾有一个同时管显示和推导的
/// <c>CommandClassNames</c>，两职合一导致筛选值与注册值互相污染，故拆开。
///
/// 5.2 删掉了 <c>IsNone</c>：它是 <c>string.IsNullOrWhiteSpace</c> 换了个名字，
/// 全仓与七个已部署模块无人调用，只有两条测试在维持它活着。
/// </remarks>
public static class CommandClassLabels
{
    /// <summary>无类直接方法在界面上的类名。</summary>
    public const string None = "无类";

    /// <summary>把类键翻译成显示标签：空串 → <see cref="None"/>。</summary>
    public static string Display(string? commandClass)
        => string.IsNullOrWhiteSpace(commandClass) ? None : commandClass.Trim().ToLowerInvariant();

    /// <summary>把显示标签翻译回类键：<see cref="None"/> → 空串。筛选比较前调用。</summary>
    public static string ToKey(string? label)
        => string.IsNullOrWhiteSpace(label) || label.Trim() == None
            ? string.Empty
            : label.Trim().ToLowerInvariant();
}

/// <summary>
/// 域聚焦下的输入解析（DEC-025 / REQ-CMD-012）。纯函数，不持有注册表也不碰界面，
/// 控制台与 Mercury 补全引擎共用同一份判定，避免两侧各写一套导致行为漂移。
/// </summary>
/// <remarks>
/// 只有两条规则：输入首段命中**已注册域**时按绝对名解析；否则补上当前聚焦域前缀。
/// 「退出聚焦」因此不需要任何域提供指令——<c>mercury.go</c>、<c>vulcan.*</c>
/// 的首段本身就是已注册域，在任何聚焦状态下都能直接输入。
///
/// 5.2 删掉了 <c>WouldPrefix</c>：它把 <see cref="Resolve"/> 的判定逐句抄了一遍，
/// 只为回答「刚才那次拼没拼前缀」——而 <c>Resolve</c> 的返回值与输入是否相等就是答案。
/// 同一条规则写在两处，改一处漏一处只是时间问题；全仓与七个已部署模块无人调用它。
/// </remarks>
public static class DomainFocus
{
    /// <summary>域筛选下拉里代表「不聚焦」的值。</summary>
    public const string All = "全部";

    /// <summary>该值是否表示未聚焦到任何域。</summary>
    public static bool IsUnfocused(string? domain)
        => string.IsNullOrWhiteSpace(domain) || domain.Trim() == All;

    /// <summary>
    /// 把用户输入解析成实际要执行的指令文本。返回值与输入不等，即表示拼了聚焦域前缀。
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
