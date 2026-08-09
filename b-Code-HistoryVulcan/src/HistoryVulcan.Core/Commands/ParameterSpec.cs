namespace HistoryVulcan.Core.Commands;

/// <summary>参数类型(校验用,§5.2 参数校验)。</summary>
public enum ParamType
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    String,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Int,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Double,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Bool,
}

/// <summary>
/// 一个指令参数的定义(§5.3:名 / 类型 / 是否必填 / 默认值 / 说明)。
/// </summary>
public sealed class ParameterSpec
{
    /// <summary>参数名(键=值 的键),小写。</summary>
    public required string Name { get; init; }

    /// <summary>帮助文本里的一句话说明。</summary>
    public required string Description { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ParamType Type { get; init; } = ParamType.String;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Required { get; init; }

    /// <summary>缺省值的文本表达(帮助显示 + 取值兜底);null 表示无默认。</summary>
    public string? Default { get; init; }

    /// <summary>
    /// 允许按位置传入时的位置序号(0 起);null 表示只能 键=值。
    /// 例:help 的 command 参数 Position=0,支持 “help vulcan.ui.dock”。
    /// </summary>
    public int? Position { get; init; }

    /// <summary>枚举型取值约束(如 pos=left/right/top/bottom/tab);null 不限。</summary>
    public string[]? AllowedValues { get; init; }
}
