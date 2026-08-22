namespace HistoryVulcan.Core.Commands;

/// <summary>指令执行结果(§5.2 生命周期终点)。</summary>
public sealed class CommandResult
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public required bool Success { get; init; }

    /// <summary>结果摘要/错误信息;可多行(控制台逐行缩进显示)。</summary>
    public required string Message { get; init; }

    /// <summary>可选的结构化载荷(供 UI 同步显示,如查询结果;M3 起使用)。</summary>
    public object? Data { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static CommandResult Ok(string message = "完成", object? data = null)
        => new() { Success = true, Message = message, Data = data };

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static CommandResult Fail(string message)
        => new() { Success = false, Message = message };
}
