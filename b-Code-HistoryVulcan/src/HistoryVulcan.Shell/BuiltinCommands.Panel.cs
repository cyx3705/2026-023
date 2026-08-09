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

public static partial class BuiltinCommands
{
    private static void RegisterPanel(CommandRegistry r, ShellCommandServices s, PanelManager panels)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.selectfile",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "选择本地文件",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var dialog = new Microsoft.Win32.OpenFileDialog();
                return dialog.ShowDialog(s.Window) == true
                    ? CommandResult.Ok($"已选择 {dialog.FileName}", dialog.FileName)
                    : CommandResult.Ok("已取消选择");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.selectdirectory",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "选择本地目录",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog();
                return dialog.ShowDialog(s.Window) == true
                    ? CommandResult.Ok($"已选择 {dialog.FolderName}", dialog.FolderName)
                    : CommandResult.Ok("已取消选择");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.panels",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "列出全部控制面板及其窗口状态",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                if (panels.Definitions.Count == 0)
                    return CommandResult.Ok("(未定义任何面板;把 JSON 放入数据目录 panels/ 下)");

                var states = s.Docking.ListWindows()
                    .ToDictionary(w => w.Id, StringComparer.OrdinalIgnoreCase);
                var sb = new StringBuilder($"共 {panels.Definitions.Count} 个面板:");
                foreach (var d in panels.Definitions)
                {
                    var state = states.TryGetValue(d.Id, out var w)
                        ? (w.IsVisible ? (w.IsFloating ? "浮动" : "停靠") : "隐藏")
                        : "未注册";
                    sb.Append($"\n  {d.Id,-12} {state}  {d.Title}({d.Controls.Count} 控件)");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.panelshow",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "显示控制面板(等价 vulcan.ui.show)",
            Example = "vulcan.ui.panelshow id=my-panel",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "id", Description = "面板 id(vulcan.ui.panels 可查)", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("id");
                if (!panels.Definitions.Any(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    var known = string.Join(" / ", panels.Definitions.Select(d => d.Id));
                    return CommandResult.Fail(
                        $"没有名为 {id} 的面板。已定义: {(known.Length > 0 ? known : "(无)")}");
                }

                s.Docking.Show(id);
                return CommandResult.Ok($"面板 {id} 已显示");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.panelset",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "程序向面板控件回写值(P-07)",
            Example = "vulcan.ui.panelset panel=my-panel control=speed value=800",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "panel", Description = "面板 id", Required = true, Position = 0 },
                new ParameterSpec { Name = "control", Description = "控件 id", Required = true, Position = 1 },
                new ParameterSpec { Name = "value", Description = "新值", Required = true, Position = 2 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var panel = ctx.RequireString("panel");
                var control = ctx.RequireString("control");
                var value = ctx.RequireString("value");

                // 面板窗口可能尚未实例化(隐藏且从未显示):先确保内容创建
                s.Docking.Show(panel);
                return panels.TrySetValue(panel, control, value, out var error)
                    ? CommandResult.Ok($"{panel}.{control} = {value}")
                    : CommandResult.Fail(error);
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.panelreload",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "重读面板 JSON 配置并原地重建(新增面板需重启)",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(panels.Reload())),
        });
    }

    // ---------------------------------------------------------------- help / cls / history / run

}

