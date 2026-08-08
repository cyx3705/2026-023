namespace HistoryVulcan.Core.Commands;

/// <summary>一行指令文本的解析结果(§5.1 语法 v0)。</summary>
public sealed class ParsedCommand
{
    /// <summary>指令名(域.动作 或 单词指令),已转小写。</summary>
    public required string Name { get; init; }

    /// <summary>位置参数,按出现顺序。</summary>
    public required IReadOnlyList<string> Positionals { get; init; }

    /// <summary>键=值参数;键不敏感大小写,值保留原始大小写。</summary>
    public required IReadOnlyDictionary<string, string> Named { get; init; }

    /// <summary>原始输入文本(回显用)。</summary>
    public required string RawText { get; init; }
}
