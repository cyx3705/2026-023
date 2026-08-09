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
