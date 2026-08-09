using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Docking;
using HistoryVulcan.Shell.Panels;

namespace HistoryVulcan.Shell;

/// <summary>内置指令组的依赖集(注册时一次性提供)。</summary>
public sealed class ShellCommandServices
{
    public required ShellWindow Window { get; init; }

    public required IDockingService Docking { get; init; }

    public required ConsoleView Console { get; init; }

    public required CommandHistory History { get; init; }

    public required ISettingsService Settings { get; init; }

    public required IShellLog Log { get; init; }

    public required CommandBus Bus { get; init; }

    public required string DataDirectory { get; init; }

    /// <summary>控制窗口群管理器(M4);null 时 panel.* 指令组不注册。</summary>
    public PanelManager? Panels { get; init; }
}
/// <summary>
/// 框架内置指令组(§5.3 / 附录 B):help / history / run /
/// app.* / log.* / win.* / layout.* 与 panel.*；cls 仅为 vulcan.log.clear 的兼容别名。
/// </summary>
public static partial class BuiltinCommands
{
    public static void Register(CommandRegistry r, ShellCommandServices s)
    {
        RegisterBasics(r, s);
        RegisterApp(r, s);
        RegisterLog(r, s);
        RegisterWin(r, s);
        RegisterLayout(r, s);
        if (s.Panels != null)
            RegisterPanel(r, s, s.Panels);
    }
}
