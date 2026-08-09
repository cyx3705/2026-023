using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Web;

/// <summary>正式 Web 接入点的配置命令。</summary>
public static class WebCommands
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static void RegisterAll(
        CommandRegistry registry,
        WebGateway gateway,
        ISettingsService settings,
        string source = "framework:web")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.web.status",
            Domain = "vulcan",
            CommandClass = "web",
            Summary = "查看 Web 服务状态",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(
                gateway.IsRunning
                    ? $"Web 服务运行中: http://{gateway.BindAddress}:{gateway.Port}/\n客户端 {gateway.ConnectedClients},Shell {gateway.ConnectedShells}"
                    : "Web 服务未运行",
                new
                {
                    running = gateway.IsRunning,
                    gateway.Port,
                    bind = gateway.BindAddress,
                    clients = gateway.ConnectedClients,
                    shells = gateway.ConnectedShells,
                })),
        }, source);

        registry.Register(SettingCommand(
            "vulcan.web.token",
            "设置 Web Bearer token(不带参数只查看是否已配置)",
            WebGateway.KeyToken,
            settings,
            secret: true), source);
        registry.Register(SettingCommand(
            "vulcan.web.bind",
            "设置 Web 绑定地址(重启服务后生效)",
            WebGateway.KeyBind,
            settings), source);
        registry.Register(SettingCommand(
            "vulcan.web.cors",
            "设置 CORS 来源白名单(逗号分隔)",
            WebGateway.KeyCors,
            settings), source);
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.web.confirm",
            Domain = "vulcan",
            CommandClass = "web",
            Summary = "设置远程确认模式(local/web；缺省 local)",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "远程确认模式",
                    Position = 0,
                    AllowedValues = ["local", "web"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode");
                if (mode == null)
                    return CommandResult.Ok($"web.confirm={settings.Get(WebGateway.KeyConfirm) ?? "local"}");
                settings.Set(WebGateway.KeyConfirm, mode);
                return CommandResult.Ok($"web.confirm={mode}");
            }),
        }, source);
    }

    private static CommandDescriptor SettingCommand(
        string name,
        string summary,
        string key,
        ISettingsService settings,
        bool secret = false)
        => new()
        {
            Name = name,
            Domain = "vulcan",
            CommandClass = "web",
            Summary = summary,
            Parameters = [new ParameterSpec { Name = "value", Description = "新的设置值", Position = 0 }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var value = ctx.GetString("value");
                if (value == null)
                {
                    var current = settings.Get(key);
                    return CommandResult.Ok(secret
                        ? (string.IsNullOrEmpty(current) ? $"{key} 未配置" : $"{key} 已配置")
                        : $"{key}={current ?? "(未配置)"}");
                }

                settings.Set(key, value);
                return CommandResult.Ok(secret ? $"{key} 已更新" : $"{key}={value}");
            }),
        };
}
