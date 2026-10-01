namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 模块调用的来源章（6.0.0，DEC-071）。
/// </summary>
/// <remarks>
/// <para>
/// 5.9.0 之前模块经总线调用时自己填来源，Diana 填的是自己的指令名，宿主只好写死信任
/// <c>diana.</c> 前缀。现在由宿主盖章：模块给的来源放进章里当「内层」，
/// <c>module:&lt;模块名&gt;</c> 或 <c>module:&lt;模块名&gt;:&lt;内层&gt;</c>。
/// </para>
/// <para>
/// 内层保留而不是覆盖，是因为模块里有转手的调用方：网关把远端请求转进总线时，
/// 内层是它给远端的标签；只看外层会把远端请求当成本机模块自己的调用。
/// 判断可信时要把章一层层剥开，看最里面那一层（见 <see cref="Innermost"/>）。
/// </para>
/// </remarks>
internal static class ModuleSource
{
    internal const string Prefix = "module:";

    /// <summary>给一个模块的调用盖章。</summary>
    public static string Stamp(string owner, string? source)
    {
        var inner = source?.Trim() ?? "";
        return inner.Length == 0 ? Prefix + owner : $"{Prefix}{owner}:{inner}";
    }

    /// <summary>
    /// 剥掉全部模块章，返回最里面那一层；整条都是模块章（没有内层）时返回空串。
    /// 不是模块章的来源原样返回。
    /// </summary>
    public static string Innermost(string source)
    {
        var current = source ?? "";
        while (current.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = current[Prefix.Length..];
            var colon = rest.IndexOf(':');
            if (colon < 0)
                return "";
            current = rest[(colon + 1)..];
        }

        return current;
    }
}
