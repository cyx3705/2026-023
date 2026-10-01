namespace HistoryVulcan.Core.Logging;

/// <summary>日志级别(C-03 六级)。</summary>
public enum ShellLogLevel
{
    /// <summary>最细的跟踪。</summary>
    Trace,
    /// <summary>调试；大段结果正文落在这一级。</summary>
    Debug,
    /// <summary>常规信息。</summary>
    Info,
    /// <summary>警告。</summary>
    Warn,
    /// <summary>错误。</summary>
    Error,
    /// <summary>致命错误。</summary>
    Fatal,
}

/// <summary>
/// 宿主交给模块的那一份日志（6.0.0，DEC-071）：只写。
/// </summary>
/// <remarks>
/// 5.5.0 起模块拿到的是宿主内部的 <c>IShellLog</c>，连缓冲快照与新纪录事件一起。
/// 控制台要读日志，改为订阅总线主题 <c>vulcan.log.entry</c> 并按需执行 <c>vulcan.log.recent</c>；
/// 写日志只需要这一个方法。
/// </remarks>
public interface IModuleLog
{
    /// <summary>写一条日志。类别是自由文本，约定以模块指令域开头（如 <c>janus.git</c>）。</summary>
    void Log(ShellLogLevel level, string category, string message);
}

/// <summary><see cref="IModuleLog"/> 的按级别便捷写法。</summary>
public static class ModuleLogExtensions
{
    /// <summary>写一条调试日志。</summary>
    public static void Debug(this IModuleLog log, string category, string message)
        => log.Log(ShellLogLevel.Debug, category, message);

    /// <summary>写一条信息日志。</summary>
    public static void Info(this IModuleLog log, string category, string message)
        => log.Log(ShellLogLevel.Info, category, message);

    /// <summary>写一条警告日志。</summary>
    public static void Warn(this IModuleLog log, string category, string message)
        => log.Log(ShellLogLevel.Warn, category, message);

    /// <summary>写一条错误日志。</summary>
    public static void Error(this IModuleLog log, string category, string message)
        => log.Log(ShellLogLevel.Error, category, message);
}

/// <summary>一条日志/指令回显记录（宿主内部）。</summary>
internal sealed record ShellLogEntry(
    DateTime Time,
    ShellLogLevel Level,
    string Category,
    string Message);

/// <summary>
/// 宿主内部的日志汇聚点：内存环形缓冲 + 事件推送 + 文件落盘。
/// 6.0.0 起不再是契约：模块只见 <see cref="IModuleLog"/>。
/// </summary>
internal interface IShellLog : IModuleLog
{
    /// <summary>新纪录到达事件。</summary>
    event EventHandler<ShellLogEntry>? EntryAdded;

    /// <summary>当前缓冲内容快照。</summary>
    IReadOnlyList<ShellLogEntry> Snapshot();
}
