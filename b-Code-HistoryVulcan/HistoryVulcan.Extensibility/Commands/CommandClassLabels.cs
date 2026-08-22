using HistoryVulcan.Core.Commands;

using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Extensibility.Commands;

/// <summary>
/// 「无类」显示标签的唯一真源（DEC-025）。两段名 <c>&lt;域&gt;.&lt;方法&gt;</c> 是该域的
/// 无类直接方法，其类为空串；目录、控制台与命令集都用这里的标签显示与筛选。
/// </summary>
/// <remarks>
/// 这里**只做显示层翻译**，不承担任何类推导。类推导的唯一入口是
/// <see cref="CommandRegistry.LegacyClass"/>；3.3.2 曾有一个同时管显示和推导的
/// <c>CommandClassNames</c>，两职合一导致筛选值与注册值互相污染，故拆开。
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

    /// <summary>该类键是否代表无类直接方法。</summary>
    public static bool IsNone(string? commandClass) => string.IsNullOrWhiteSpace(commandClass);
}
