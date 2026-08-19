using System.Diagnostics;
using System.IO;
using System.Text;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

public static class ServiceCommands
{
    public static void RegisterAll(
        CommandRegistry registry,
        ServiceComposition composition,
        Action requestStop,
        string executablePath,
        string source = "framework:service",
        IReadOnlyList<string>? serviceArguments = null)
    {
        serviceArguments ??= [];

        // REQ-A6：从前端搬回服务侧。它逐行把脚本喂给总线，只依赖 Bus 与数据根，
        // 没有任何 UI 依赖（原实现连 RequiresUiThread 都没标）。留在前端只会让
        // 一条纯总线能力随前端一起被切出去，还得为此在 aurora 域下重新命名。
        registry.Register(new CommandDescriptor
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
                var root = composition.DataDirectory;
                if (!Path.IsPathRooted(raw) && string.IsNullOrEmpty(root))
                    return CommandResult.Fail("未配置应用数据根，无法解析相对脚本路径");

                var path = Path.IsPathRooted(raw) ? raw : Path.Combine(root!, raw);
                if (!File.Exists(path))
                    return CommandResult.Fail($"脚本不存在: {path}");

                var scriptSource = $"脚本:{Path.GetFileName(path)}";
                var keepGoing = ctx.GetBool("continue");
                var ok = 0;
                var failed = 0;

                var lines = await File.ReadAllLinesAsync(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (CommandParser.IsBlankOrComment(lines[i]))
                        continue;

                    var result = await composition.Bus.ExecuteAsync(lines[i], scriptSource);
                    if (result.Success)
                    {
                        ok++;
                    }
                    else
                    {
                        failed++;
                        if (!keepGoing)
                            return CommandResult.Fail(
                                $"第 {i + 1} 行失败,脚本已中断(continue=true 可跳过错误): {lines[i]}");
                    }
                }

                return failed == 0
                    ? CommandResult.Ok($"脚本执行完成: {ok} 条成功")
                    : CommandResult.Ok($"脚本执行完成: {ok} 条成功,{failed} 条失败(已跳过)");
            },
        }, source);

        // REQ-A6 / DEC-006：与 command.run 同类——只查注册表并拼文本，无任何 UI 依赖。
        // 留在前端会让"查指令帮助"这种基础能力依赖界面进程在线，且它与服务侧已有的
        // command.list / show / domains 本属同一族（都是注册表查询），分处两个进程没有道理。
        // 服务侧注册表含前端投影的能力目录，因此这里的输出比前端版本更完整。
        registry.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.command.help",
            CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.GetString("command");
                return name == null
                    ? HelpList(composition.Registry)
                    : HelpDetail(composition.Registry, name);
            })), source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.svc.status",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "查看服务进程状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("服务运行中", new
            {
                running = true,
                processId = Environment.ProcessId,
                mcp = composition.Mcp?.IsRunning ?? false,
                web = composition.Web?.IsRunning ?? false,
                modules = composition.Modules?.Modules.Count ?? 0,
            })),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.svc.stop",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "停止服务进程",
            ConfirmPrompt = _ => "确认停止后台服务？前端和远程客户端会断开。",
            Handler = CommandDescriptor.Sync(_ =>
            {
                requestStop();
                return CommandResult.Ok("服务正在停止");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.quit",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "退出 HistoryVulcan 前端与后台服务",
            ConfirmPrompt = _ => "确认退出 HistoryVulcan 前端和后台服务？",
            Handler = async ctx =>
            {
                var frontend = composition.Web?.ConnectedShells > 0
                    ? await composition.Web.RelayFrontendCommandAsync(
                        "vulcan.app.close", ctx.Source, ctx.Cancellation).ConfigureAwait(false)
                    : CommandResult.Ok("前端未连接");
                requestStop();
                return frontend.Success
                    ? CommandResult.Ok("HistoryVulcan 正在退出")
                    : CommandResult.Ok($"后台正在退出，前端回执: {frontend.Message}");
            },
        }, source);

        RegisterFrontendLifecycle(registry, composition, executablePath, source);


        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.svc.restart",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "重启服务进程",
            ConfirmPrompt = _ => "确认重启后台服务？客户端会短暂断开。",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var start = new ProcessStartInfo(executablePath) { UseShellExecute = true };
                foreach (var argument in serviceArguments)
                    start.ArgumentList.Add(argument);
                Process.Start(start);
                requestStop();
                return CommandResult.Ok("服务正在重启");
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.svc.autostart",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "查看或设置用户级登录启动",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "登录启动开关",
                    Position = 0,
                    AllowedValues = ["on", "off"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var manager = composition.Autostart;
                if (manager == null)
                    return CommandResult.Fail("当前宿主未配置自启动管理器");
                var mode = ctx.GetString("mode");
                if (mode == null)
                    return CommandResult.Ok(IsAutostartEnabled(composition)
                        ? "服务登录启动已开启"
                        : "服务登录启动已关闭");

                var enabled = mode.Equals("on", StringComparison.OrdinalIgnoreCase);
                composition.Settings.Set("svc.autostart", enabled ? "true" : "false");
                manager.SetEnabled(
                    composition.ServiceName,
                    executablePath,
                    serviceArguments,
                    enabled);
                return CommandResult.Ok($"服务登录启动已{(enabled ? "开启" : "关闭")}");
            }),
        }, source);
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

    private static bool IsAutostartEnabled(ServiceComposition composition)
        => composition.Settings.Get("svc.autostart") is not { } value
           || !bool.TryParse(value, out var enabled)
           || enabled;

    private static void RegisterFrontendLifecycle(
        CommandRegistry registry,
        ServiceComposition composition,
        string executablePath,
        string source)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.focusconsole",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示并聚焦控制台；必要时冷启动前端",
            Handler = ctx => RelayOrStartAsync(
                composition,
                executablePath,
                "vulcan.app.focusconsole",
                "--focus-console",
                ctx),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.show",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示并激活前端窗口",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "startup",
                    Description = "冷启动参数：--show（默认）或 --focus-console",
                    Required = false,
                },
            ],
            Handler = ctx =>
            {
                if (!TryNormalizeFrontendStartup(ctx.GetString("startup"), out var startup, out var error))
                    return Task.FromResult(CommandResult.Fail(error));
                // 已连接时只中继裸指令，避免把 startup 传到前端（前端 show 无此参数）。
                return RelayOrStartAsync(
                    composition,
                    executablePath,
                    "vulcan.app.show",
                    startup,
                    ctx);
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.hide",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "隐藏前端窗口并保持后台运行",
            Handler = ctx => RelayOrStartAsync(composition, executablePath, "vulcan.app.hide", null, ctx),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.close",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "退出前端进程",
            Handler = async ctx =>
            {
                var web = composition.Web;
                if (web == null || web.ConnectedShells <= 0)
                    return CommandResult.Ok("前端未连接");
                return await web.RelayFrontendCommandAsync(
                    "vulcan.app.close", ctx.Source, ctx.Cancellation).ConfigureAwait(false);
            },
        }, source);
    }

    private static async Task<CommandResult> RelayOrStartAsync(
        ServiceComposition composition,
        string executablePath,
        string command,
        string? startupArgument,
        CommandContext context)
    {
        if (composition.Web?.ConnectedShells > 0)
            return await composition.Web.RelayFrontendCommandAsync(
                command, context.Source, context.Cancellation).ConfigureAwait(false);

        if (startupArgument == null)
            return CommandResult.Fail("前端未连接");

        try
        {
            Process.Start(new ProcessStartInfo(executablePath, startupArgument)
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return CommandResult.Ok("前端正在启动");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Fail($"前端启动失败: {ex.Message}");
        }
    }

    private static bool TryNormalizeFrontendStartup(
        string? startup,
        out string normalized,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(startup)
            || startup.Equals("--show", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "--show";
            error = "";
            return true;
        }

        if (startup.Equals("--focus-console", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "--focus-console";
            error = "";
            return true;
        }

        normalized = "--show";
        error = "startup 仅允许 --show 或 --focus-console。";
        return false;
    }
}
