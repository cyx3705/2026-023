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
    private static void RegisterBasics(CommandRegistry r, ShellCommandServices s)
    {
        r.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.command.help",
            CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.GetString("command");
                return name == null ? HelpList(s.Bus.Registry) : HelpDetail(s.Bus.Registry, name);
            })));

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.command.copyexample",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "复制指定命令的示例",
            Example = "vulcan.command.copyexample name=vulcan.log.level",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec { Name = "name", Description = "命令名", Required = true, Position = 0 },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name");
                if (!s.Bus.Registry.TryGet(name, out var descriptor))
                    return CommandResult.Fail($"未知指令: {name}");
                if (string.IsNullOrWhiteSpace(descriptor.Example))
                    return CommandResult.Fail($"{name} 没有示例");
                try
                {
                    Clipboard.SetText(descriptor.Example);
                    return CommandResult.Ok($"已复制 {name} 的示例");
                }
                catch (Exception ex)
                {
                    return CommandResult.Fail($"复制失败: {ex.Message}");
                }
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.command.history",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "查看指令历史",
            Readonly = true,
            Example = "vulcan.command.history count=10",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "显示条数",
                    Type = ParamType.Int,
                    Default = "20",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var count = Math.Max(1, ctx.GetInt("count", 20));
                var items = s.History.Snapshot();
                if (items.Count == 0)
                    return CommandResult.Ok("(历史为空)");

                var start = Math.Max(0, items.Count - count);
                var sb = new StringBuilder($"最近 {items.Count - start} 条(共 {items.Count} 条):");
                for (var i = start; i < items.Count; i++)
                    sb.Append($"\n{i + 1,4}  {items[i]}");
                return CommandResult.Ok(sb.ToString());
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.command.run",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "逐行执行指令脚本文件(# 注释与空行忽略)",
            Example = "vulcan.command.run file=每日巡检.txt continue=true",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "file",
                    Description = "脚本路径;相对路径基于应用数据目录",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "continue",
                    Description = "出错时跳过继续(默认中断并报告行号)",
                    Type = ParamType.Bool,
                    Default = "false",
                },
            ],
            Handler = async ctx =>
            {
                var raw = ctx.RequireString("file");
                var path = Path.IsPathRooted(raw)
                    ? raw
                    : Path.Combine(s.DataDirectory, raw);
                if (!File.Exists(path))
                    return CommandResult.Fail($"脚本不存在: {path}");

                var source = $"脚本:{Path.GetFileName(path)}";
                var keepGoing = ctx.GetBool("continue");
                var ok = 0;
                var failed = 0;

                var lines = await File.ReadAllLinesAsync(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (CommandParser.IsBlankOrComment(lines[i]))
                        continue;

                    var result = await s.Bus.ExecuteAsync(lines[i], source);
                    if (result.Success)
                    {
                        ok++;
                    }
                    else
                    {
                        failed++;
                        if (!keepGoing)
                            return CommandResult.Fail($"第 {i + 1} 行失败,脚本已中断(continue=true 可跳过错误): {lines[i]}");
                    }
                }

                return failed == 0
                    ? CommandResult.Ok($"脚本执行完成: {ok} 条成功")
                    : CommandResult.Ok($"脚本执行完成: {ok} 条成功,{failed} 条失败(已跳过)");
            },
        });
    }

    private static CommandResult HelpList(CommandRegistry registry)
    {
        var all = registry.All();
        var sb = new StringBuilder($"共 {all.Count} 条指令,vulcan.command.help <指令名> 查看详情:");
        foreach (var group in all.GroupBy(d =>
                 {
                     var dot = d.Name.IndexOf('.');
                     return dot > 0 ? d.Name[..dot] : "基础";
                 }, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append($"\n[{group.Key}] ({group.Count()})");
            foreach (var d in group)
                sb.Append($"\n  {d.Name,-24} {d.Summary}");
        }

        return CommandResult.Ok(sb.ToString());
    }

    private static CommandResult HelpDetail(CommandRegistry registry, string name)
    {
        if (!registry.TryGet(name, out var d))
        {
            var suggestions = registry.Suggest(name);
            var hint = suggestions.Count > 0 ? $"\n相近指令: {string.Join(" / ", suggestions)}" : "";
            return CommandResult.Fail($"未知指令: {name}{hint}");
        }

        var sb = new StringBuilder($"{d.Name} —— {d.Summary}");

        if (d.Parameters.Count == 0)
        {
            sb.Append("\n参数: (无)");
        }
        else
        {
            sb.Append("\n参数:");
            foreach (var p in d.Parameters)
            {
                var attrs = new List<string>();
                if (p.Required)
                    attrs.Add("必填");
                if (p.AllowedValues is { Length: > 0 })
                    attrs.Add(string.Join("/", p.AllowedValues));
                if (p.Default != null)
                    attrs.Add($"默认{p.Default}");
                attrs.Add(p.Type.ToString().ToLowerInvariant());
                var suffix = attrs.Count > 0 ? $"({string.Join(",", attrs)})" : "";
                sb.Append($"\n  {p.Name + suffix,-28} {p.Description}");
            }
        }

        sb.Append($"\n{CommandBus.FormatUsage(d)}");
        if (d.Example != null)
            sb.Append($"\n示例: {d.Example}");
        if (d.IsDangerous)
            sb.Append("\n安全: 执行动作可能要求本地二次确认");
        if (d.RequiresUiThread)
            sb.Append("\n线程: UI");
        return CommandResult.Ok(sb.ToString());
    }

    // ---------------------------------------------------------------- app.*

}

