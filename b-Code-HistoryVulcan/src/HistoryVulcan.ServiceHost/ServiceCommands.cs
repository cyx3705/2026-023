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
            Name = "vulcan.svc.frontends",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "列出仍被缓存的前端能力目录",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var web = composition.Web;
                if (web == null)
                    return CommandResult.Fail("网关未启用");
                var cached = web.CachedFrontendCatalogs();
                if (cached.Count == 0)
                    return CommandResult.Ok("无缓存的前端目录");
                return CommandResult.Ok(string.Join(
                    Environment.NewLine,
                    cached.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(pair => $"{pair.Key}  {pair.Value} 条")));
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.svc.forgetfrontend",
            Domain = "vulcan",
            CommandClass = "svc",
            Summary = "忘掉某个前端的缓存目录并撤销其代理指令",
            Example = "vulcan.svc.forgetfrontend name=HistoryVulcan.Frontend",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "vulcan.svc.frontends 中列出的前端名",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var web = composition.Web;
                if (web == null)
                    return CommandResult.Fail("网关未启用");
                var name = ctx.GetString("name")?.Trim();
                if (string.IsNullOrWhiteSpace(name))
                    return CommandResult.Fail("缺少 name");
                var removed = web.ForgetFrontendCatalog(name);
                return removed < 0
                    ? CommandResult.Fail($"没有名为 {name} 的缓存目录")
                    : CommandResult.Ok($"已忘掉 {name}，撤销 {removed} 条指令");
            }),
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

        RegisterFrontendLifecycle(registry, composition, source);


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
        string source)
    {
        registry.Register(Lifecycle(composition, "vulcan.app.focusconsole", "显示并聚焦控制台"), source);
        registry.Register(Lifecycle(composition, "vulcan.app.show", "显示并激活界面窗口"), source);
        registry.Register(Lifecycle(composition, "vulcan.app.hide", "隐藏界面窗口并保持后台运行"), source);
        registry.Register(Lifecycle(composition, "vulcan.app.close", "关闭界面窗口"), source);
    }

    /// <summary>
    /// 界面生命周期命令：一律**中继**，绝不派生进程。
    ///
    /// 4.0.0 之前这里会在前端未连接时 <c>Process.Start(executablePath, "--show")</c>，
    /// 而 <c>executablePath</c> 是**宿主自己**。宿主无头化后 <c>--show</c> 是未知参数被静默忽略，
    /// 于是那条路径变成"再起一个无头服务"——真机上确实留下过一个多余的宿主进程，
    /// 而用户看到的是"点了没反应"。
    ///
    /// Aurora DEC-008 之后界面是宿主装载的模块，跟宿主同生共死，本就没有"冷启动前端"这回事：
    /// 界面不在，就是模块没装，派生任何进程都不会让它出现。因此这里只剩两条出路——
    /// 有前端就中继，没有就明确失败。
    ///
    /// 两个中继口都不针对具体产品：外部前端走网关，进程内界面走
    /// <see cref="CommandBus.FrontendExecutor"/>——谁登记了自己是前端就转给谁。
    /// </summary>
    private static CommandDescriptor Lifecycle(
        ServiceComposition composition,
        string name,
        string summary)
        => new()
        {
            Name = name,
            Domain = "vulcan",
            CommandClass = "app",
            Summary = summary,
            Handler = async context =>
            {
                var web = composition.Web;
                if (web != null && web.ConnectedShells > 0)
                    return await web.RelayFrontendCommandAsync(
                        name, context.Source, context.Cancellation).ConfigureAwait(false);

                if (composition.Bus.FrontendExecutor is { } frontend)
                    return await frontend(name, context.Source, context.Cancellation).ConfigureAwait(false);

                return CommandResult.Fail("界面未装载：未发现进程内界面，也没有已连接的外部前端");
            },
        };

}
