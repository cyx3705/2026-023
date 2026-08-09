namespace HistoryVulcan.Core.Docking;

/// <summary>
/// 一条等价指令的文本表达(§5.1 语法),及其来源类别(C-01 来源标签)。
/// </summary>
public sealed class ShellCommandEventArgs : EventArgs
{
    /// <summary>指令文本,如 "vulcan.ui.dock name=console pos=bottom ratio=0.28"。</summary>
    public required string CommandText { get; init; }

    /// <summary>来源类别:"layout"(拖拽手势)、"UI"(菜单/按钮)等。</summary>
    public required string Source { get; init; }
}
