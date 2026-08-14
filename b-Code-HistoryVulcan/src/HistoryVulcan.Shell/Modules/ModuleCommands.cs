using HistoryVulcan.Services.Modules;
using System.Diagnostics;
using System.IO;
using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Shell.Modules;

/// <summary>module.* 管理指令(MD-05):list / reload / roots / open。</summary>
public static class ModuleCommands
{
    /// <summary>Legacy settings key retained for binary compatibility; standalone hosts ignore it.</summary>
    public const string KeyModuleDir = "module.dir";

    /// <summary>Settings key containing automatic or explicit Z discovery roots.</summary>
    public const string KeyModuleRoots = "module.roots";

    public static void RegisterAll(
        CommandRegistry registry, ModuleHost host, ISettingsService settings, string source = "app")
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.list",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "列出已加载模块(名称/版本/描述/指令数)",
            Readonly = true,
            Example = "vulcan.module.list",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var modules = host.Modules;
                if (modules.Count == 0)
                    return CommandResult.Ok("当前无已加载模块。请检查 Z manifest 与 vulcan.module.roots 诊断。");

                var sb = new StringBuilder();
                sb.Append($"已加载 {modules.Count} 个模块:");
                foreach (var m in modules)
                {
                    sb.Append($"\n  {m.ModuleName} {m.Version}  [{(m.Open ? "全暴露" : "精准暴露")}]" +
                              $"  {m.CommandCount} 条指令  ← {(m.Slot.Length > 0 ? m.Slot + "/" : "")}{m.AssemblyFile}" +
                              $"{(m.Slot.Length > 0 ? "(槽)" : "(根)")}");
                    if (m.Description.Length > 0)
                        sb.Append($"\n      {m.Description}");
                }

                return CommandResult.Ok(sb.ToString(), modules);
            }),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.reload",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "手动整体重载全部模块(文件变化会自动热重载,通常无需手动)",
            Example = "vulcan.module.reload",
            Handler = async _ =>
            {
                await Task.Run(host.Reload);
                return CommandResult.Ok(
                    $"重载完成: {host.Modules.Count} 个模块,{host.Modules.Sum(m => m.CommandCount)} 条指令");
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.roots",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "查看或设置 Z 模块发现根",
            Example = "vulcan.module.roots paths=auto",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "paths",
                    Description = "分号分隔的绝对根；auto 恢复自动识别；省略时查询",
                    Position = 0,
                },
            ],
            Handler = async ctx =>
            {
                var paths = ctx.GetString("paths");
                if (string.IsNullOrWhiteSpace(paths))
                    return CommandResult.Ok($"当前模块发现根: {string.Join(";", host.DiscoveryRoots)}");
                var roots = ResolveRoots(paths);
                settings.Set(KeyModuleRoots, paths.Equals("auto", StringComparison.OrdinalIgnoreCase)
                    ? "auto"
                    : string.Join(';', roots));
                await Task.Run(() => host.ChangeDiscoveryRoots(roots));
                return CommandResult.Ok($"模块发现根已切换并重载: {string.Join(";", roots)}");
            },
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.open",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "在系统资源管理器中打开模块目录(UI-12 面板按钮落点)",
            Example = "vulcan.module.open",
            Handler = CommandDescriptor.Sync(_ =>
            {
                var root = host.DiscoveryRoots.FirstOrDefault();
                if (root == null)
                    return CommandResult.Fail("当前没有可打开的模块发现根");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"")
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开模块发现根: {root}");
            }),
        }, source);
    }

    private static IReadOnlyList<string> ResolveRoots(string paths)
    {
        if (paths.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var root = ZModuleDiscoverySource.ResolveAutomaticRoot()
                       ?? throw new InvalidOperationException(ZModuleDiscoverySource.AutomaticRootNotFound);
            return [root];
        }

        var values = paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0 || values.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("vulcan.module.roots 只接受分号分隔的绝对路径或 auto。");
        var roots = values
            .Select(ZModuleDiscoverySource.CoerceConfiguredRoot)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return roots;
    }
}
