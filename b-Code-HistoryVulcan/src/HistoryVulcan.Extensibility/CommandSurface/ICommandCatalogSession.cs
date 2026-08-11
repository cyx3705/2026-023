namespace HistoryVulcan.Extensibility.CommandSurface;

/// <summary>命令目录会话筛选状态（控制台与命令集共用）。</summary>
public sealed record CommandCatalogFilter(
    string Query = "",
    string Domain = "全部",
    string CommandClass = "全部",
    int McpFilter = 0);

/// <summary>目录会话变更种类。</summary>
public enum CommandCatalogChangeKind
{
    /// <summary>快照重载。</summary>
    Snapshot,
    /// <summary>筛选变更。</summary>
    Filter,
    /// <summary>选择变更。</summary>
    Selection,
    /// <summary>注册表失效，需刷新。</summary>
    Invalidated,
}

/// <summary>目录会话变更参数。</summary>
public sealed class CommandCatalogChangedEventArgs(CommandCatalogChangeKind kind) : EventArgs
{
    /// <summary>变更种类。</summary>
    public CommandCatalogChangeKind Kind { get; } = kind;
}

/// <summary>
/// 控制台补全与命令集/详情共享的最小目录会话面。
/// 具体实现由命令工作台模块（Mercury）提供；Shell 仅持有延迟代理。
/// </summary>
public interface ICommandCatalogSession : IDisposable
{
    /// <summary>目录或筛选变化。</summary>
    event EventHandler<CommandCatalogChangedEventArgs>? Changed;

    /// <summary>当前可见域列表（不含「全部」）。</summary>
    IReadOnlyList<string> Domains { get; }

    /// <summary>当前域下的类列表（不含「全部」）；域为全部时为空。</summary>
    IReadOnlyList<string> Classes { get; }

    /// <summary>当前选中的命令名。</summary>
    string? SelectedCommandName { get; }

    /// <summary>当前筛选。</summary>
    CommandCatalogFilter CurrentFilter { get; }

    /// <summary>刷新目录快照。</summary>
    Task<bool> RefreshAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>设置完整筛选。</summary>
    void SetFilter(CommandCatalogFilter filter);

    /// <summary>尝试设置域筛选。</summary>
    bool TrySetDomain(string domain, out IReadOnlyList<string> availableDomains);

    /// <summary>尝试设置类筛选。</summary>
    bool TrySetCommandClass(string commandClass, out IReadOnlyList<string> availableClasses);

    /// <summary>设置控制台检索词（同步命令集列表）。</summary>
    void SetConsoleQuery(string query);

    /// <summary>在可见行中移动选择。</summary>
    bool MoveSelection(int direction);

    /// <summary>选中指定命令。</summary>
    void Select(string? commandName);

    /// <summary>为控制台输入计算补全。</summary>
    Task<ConsoleCompletionResult> CompleteAsync(
        string text,
        int caretIndex,
        CancellationToken cancellationToken = default);
}
