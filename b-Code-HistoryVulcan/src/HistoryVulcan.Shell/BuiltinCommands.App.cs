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
    private static void RegisterApp(CommandRegistry r, ShellCommandServices s)
    {
        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.app.quit",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "退出程序",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                s.Window.Close();
                return CommandResult.Ok("正在退出");
            }),
        });

        RegisterFrontend(r, new CommandDescriptor
        {
            Name = "vulcan.app.about",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示关于对话框",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                MessageBox.Show(s.Window, s.Window.AboutText, "关于",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return CommandResult.Ok("已显示关于");
            }),
        });

        r.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.opendata",
            CommandDescriptor.Sync(_ =>
            {
                Process.Start(new ProcessStartInfo("explorer.exe", s.DataDirectory)
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开 {s.DataDirectory}");
            })));

        r.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.set",
            CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.RequireString("key");
                var value = ctx.RequireString("value");
                s.Settings.Set(key, value);
                return CommandResult.Ok($"{key} = {DisplaySettingValue(key, value)}");
            })));

        r.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.get",
            CommandDescriptor.Sync(ctx =>
            {
                var key = ctx.GetString("key");
                if (key != null)
                {
                    var value = s.Settings.Get(key);
                    return value == null
                        ? CommandResult.Ok($"{key} (未设置)")
                        : CommandResult.Ok($"{key} = {DisplaySettingValue(key, value)}");
                }

                var all = s.Settings.All();
                if (all.Count == 0)
                    return CommandResult.Ok("(无配置项)");
                return CommandResult.Ok(
                    $"共 {all.Count} 项:" + string.Concat(
                        all.Select(kv => $"\n  {kv.Key} = {DisplaySettingValue(kv.Key, kv.Value)}")));
            })));
    }

    /// <summary>
    /// 令牌类设置值一律以占位符回报(FZR-01)。
    ///
    /// 脱敏必须落在指令结果本身,而不是总线的日志回显:`vulcan.app.get` 声明了 Readonly,
    /// 因而对 scope=read 的远程设备放行,并在默认 readonly 策略下作为 MCP 工具可见。
    /// 结果对象会原样序列化进 HTTP 响应体与 tools/call 载荷,只脱敏日志挡不住这两条路径。
    ///
    /// 判定谓词与 <c>CommandBus.IsSensitiveSettingKey</c> 同源。冻结期不为共享它新增
    /// 公开 API 或 InternalsVisibleTo,故此处保留一份副本;3.1 统一到单一真值(见整改清单 FZR-22)。
    /// </summary>
    private static string DisplaySettingValue(string key, string value)
        => IsSensitiveSettingKey(key) ? "(已配置)" : value;

    private static bool IsSensitiveSettingKey(string key)
    {
        var normalized = key.Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- log.*


    // ---------------------------------------------------------------- win.*

}

