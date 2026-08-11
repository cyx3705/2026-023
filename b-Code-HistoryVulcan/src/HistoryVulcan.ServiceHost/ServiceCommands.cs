using System.Diagnostics;
using System.Windows;
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
                Application.Current.Dispatcher.BeginInvoke(requestStop);
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
                _ = Application.Current.Dispatcher.BeginInvoke(requestStop);
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
                Application.Current.Dispatcher.BeginInvoke(requestStop);
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
                    return CommandResult.Ok(manager.IsEnabled(composition.ServiceName)
                        ? "服务登录启动已开启"
                        : "服务登录启动已关闭");

                manager.SetEnabled(
                    composition.ServiceName,
                    executablePath,
                    serviceArguments,
                    mode.Equals("on", StringComparison.OrdinalIgnoreCase));
                return CommandResult.Ok($"服务登录启动已{(mode == "on" ? "开启" : "关闭")}");
            }),
        }, source);
    }

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
