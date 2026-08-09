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
    private static void RegisterWin(CommandRegistry r, ShellCommandServices s)
    {
        var nameParam = new ParameterSpec
        {
            Name = "name",
            Description = "窗口名(vulcan.ui.windows 可查)",
            Required = true,
            Position = 0,
        };

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.windows",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "列出全部窗口及状态",
            Readonly = true,
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var sb = new StringBuilder("窗口清单:");
                foreach (var w in s.Docking.ListWindows())
                {
                    var state = !w.IsVisible ? "隐藏"
                        : w.IsFloating ? "浮动"
                        : w.Side switch
                        {
                            DockSide.Left => "停靠·左",
                            DockSide.Right => "停靠·右",
                            DockSide.Top => "停靠·上",
                            DockSide.Bottom => "停靠·下",
                            DockSide.Center => "中央区",
                            DockSide.Tab => "标签组",
                            _ => "停靠",
                        };
                    var ratio = w.Ratio is { } v and > 0 ? $" {v:P0}" : "";
                    var maximized = s.Docking.MaximizedId?.Equals(
                        w.Id, StringComparison.OrdinalIgnoreCase) == true ? " [最大化]" : "";
                    sb.Append($"\n  {w.Id,-12} {state}{ratio}{maximized}  {w.Title}  owner={w.Owner}");
                }

                return CommandResult.Ok(sb.ToString());
            }),
        });

        RegisterWindowVerb(r, s, "vulcan.ui.show", "显示窗口(隐藏则唤出,已显示则激活)",
            (d, id) => { d.Show(id); return $"{id} 已显示"; });
        RegisterWindowVerb(r, s, "vulcan.ui.hide", "隐藏窗口(状态保留,可再唤出)",
            (d, id) => { d.Hide(id); return $"{id} 已隐藏"; });
        RegisterWindowVerb(r, s, "vulcan.ui.float", "把窗口浮动为独立顶层窗口",
            (d, id) => { d.Float(id); return $"{id} 已浮动"; });
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.autohide",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "切换工具窗口的自动隐藏状态",
            Example = $"vulcan.ui.autohide name={StandardWindowIds.Console}",
            RequiresUiThread = true,
            Parameters = [nameParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                if (s.Docking is not DockingHost host)
                    return CommandResult.Fail("当前停靠宿主不支持自动隐藏");

                var id = ctx.RequireString("name");
                try
                {
                    host.ToggleAutoHide(id);
                    return CommandResult.Ok($"{id} 已切换自动隐藏状态");
                }
                catch (InvalidOperationException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
            }),
        });
        RegisterWindowVerb(r, s, "vulcan.ui.reset", "把窗口复位到注册时的默认位置",
            (d, id) => { d.ResetWindow(id); return $"{id} 已复位到默认位置"; });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.max",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "最大化指定工具窗口",
            Example = "vulcan.ui.max name=se2sw",
            RequiresUiThread = true,
            Parameters = [nameParam],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var id = ctx.RequireString("name");
                var exists = s.Docking.ListWindows().Any(w =>
                    w.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    return CommandResult.Fail($"没有名为 {id} 的窗口");
                s.Docking.MaximizeWindow(id);
                return CommandResult.Ok($"{id} 已最大化");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.floatstate",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "设置独立浮窗宿主的最大化状态",
            Example = $"vulcan.ui.floatstate name={StandardWindowIds.Console} state=toggle",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "state",
                    Description = "maximized、normal 或 toggle",
                    Default = "toggle",
                    Position = 1,
                    AllowedValues = ["maximized", "normal", "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;
                return s.Window.SetFloatingWindowState(
                    ctx.RequireString("name"), ctx.GetString("state") ?? "toggle");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.restore",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "退出窗口最大化并恢复原布局",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Docking.RestoreLayoutFromMaximized();
                return CommandResult.Ok("已恢复原布局");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.dock",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "停靠窗口到指定方位(pos=center 占中央区，pos=tab 并入目标标签组)",
            Example = $"vulcan.ui.dock name={StandardWindowIds.Console} pos=bottom ratio=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "pos",
                    Description = "停靠方位",
                    Required = true,
                    AllowedValues = ["left", "right", "top", "bottom", "center", "tab"],
                },
                new ParameterSpec { Name = "target", Description = "pos=tab 时,并入哪个窗口所在的标签组" },
                new ParameterSpec
                {
                    Name = "ratio",
                    Description = "四边停靠比例；提供时须严格位于 (0,1)，Center/Tab 不使用",
                    Type = ParamType.Double,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var id = ctx.RequireString("name");
                var side = ParseSide(ctx.RequireString("pos"));
                var ratio = ctx.Has("ratio") ? ctx.GetDouble("ratio") : (double?)null;
                if (ratio is { } ratioValue &&
                    (!double.IsFinite(ratioValue) || ratioValue is <= 0 or >= 1))
                    return CommandResult.Fail($"ratio 应严格位于 (0,1),实际: {ratio}");

                var target = ctx.GetString("target");
                if (side == DockSide.Tab && target == null)
                    return CommandResult.Fail("pos=tab 时必须指定 target=(并入哪个窗口的标签组)");

                s.Docking.Dock(id, side, ratio, target);
                var where = side switch
                {
                    DockSide.Left => "左侧",
                    DockSide.Right => "右侧",
                    DockSide.Top => "顶部",
                    DockSide.Bottom => "底部",
                    DockSide.Center => "中央区",
                    _ => $"{target} 所在标签组",
                };
                var pct = ratio is { } rv ? $"({rv:P0})" : "";
                return CommandResult.Ok($"{id} 已停靠至{where}{pct}");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.ui.ratio",
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = "调整窗口占主窗体的比例",
            Example = $"vulcan.ui.ratio name={StandardWindowIds.Console} value=0.3",
            RequiresUiThread = true,
            Parameters =
            [
                nameParam,
                new ParameterSpec
                {
                    Name = "value",
                    Description = "四边停靠比例，须严格位于 (0,1)",
                    Required = true,
                    Type = ParamType.Double,
                    Position = 1,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;

                var value = ctx.GetDouble("value");
                if (!double.IsFinite(value) || value is <= 0 or >= 1)
                    return CommandResult.Fail($"value 应严格位于 (0,1),实际: {value}");

                var id = ctx.RequireString("name");
                s.Docking.SetRatio(id, value);
                return CommandResult.Ok($"{id} 比例已调整为 {value:P0}");
            }),
        });
    }

    private static void RegisterWindowVerb(
        CommandRegistry r,
        ShellCommandServices s,
        string name,
        string summary,
        Func<IDockingService, string, string> action)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = name,
            Domain = "vulcan",
            CommandClass = "ui",
            Summary = summary,
            Example = $"{name} name={StandardWindowIds.Console}",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "窗口名(vulcan.ui.windows 可查)",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                if (ResolveWindow(s, ctx) is { } error)
                    return error;
                var id = ctx.RequireString("name");
                var message = action(s.Docking, id);
                if (name.Equals("vulcan.ui.show", StringComparison.OrdinalIgnoreCase))
                    s.Window.ActivateToolContent(id);
                return CommandResult.Ok(message);
            }),
        });
    }

    /// <summary>窗口名存在性校验;返回 null 表示通过。</summary>
    private static CommandResult? ResolveWindow(ShellCommandServices s, CommandContext ctx)
    {
        var id = ctx.RequireString("name");
        if (s.Docking.ListWindows().Any(w => w.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            return null;

        var known = string.Join(" / ", s.Docking.ListWindows().Select(w => w.Id));
        return CommandResult.Fail($"没有名为 {id} 的窗口。已注册: {known}");
    }

    private static DockSide ParseSide(string pos) => pos.ToLowerInvariant() switch
    {
        "left" => DockSide.Left,
        "right" => DockSide.Right,
        "top" => DockSide.Top,
        "bottom" => DockSide.Bottom,
        "center" => DockSide.Center,
        _ => DockSide.Tab,
    };

    // ---------------------------------------------------------------- layout.*

}

