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
        _ = settings;
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
            Name = "vulcan.module.install",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "从已校验候选包原子安装并重载运行时模块",
            Example = "vulcan.module.install path=C:\\candidate\\HistoryJanus",
            Dangerous = true,
            Parameters = [new ParameterSpec
            {
                Name = "path",
                Description = "含 module.manifest.json 与完整 SHA256SUMS 的绝对包目录",
                Required = true,
                Position = 0,
            }],
            Handler = CommandDescriptor.Sync(ctx =>
                IsLocalMutationSource(ctx.Source)
                    ? host.InstallPackage(ctx.RequireString("path"))
                    : CommandResult.Fail("模块安装只允许本机宿主命令路径。")),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.remove",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "从运行区原子移除模块包并刷新运行快照",
            Example = "vulcan.module.remove name=HistoryJanus",
            Dangerous = true,
            Parameters = [new ParameterSpec
            {
                Name = "name",
                Description = "vulcan.module.list 中的模块名",
                Required = true,
                Position = 0,
            }],
            Handler = CommandDescriptor.Sync(ctx =>
                IsLocalMutationSource(ctx.Source)
                    ? host.RemovePackage(ctx.RequireString("name"))
                    : CommandResult.Fail("模块移除只允许本机宿主命令路径。")),
        }, source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.roots",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "查看固定的运行时模块目录（兼容查询）",
            Example = "vulcan.module.roots",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "paths",
                    Description = "兼容参数；3.12.0 起拒绝修改",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var paths = ctx.GetString("paths");
                if (string.IsNullOrWhiteSpace(paths))
                    return CommandResult.Ok($"固定运行时模块目录: {host.ModulesDirectory}");
                return CommandResult.Fail(
                    $"3.12.0 起模块目录固定为 {host.ModulesDirectory}；module.roots 设置不再生效。");
            }),
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
                var root = host.ModulesDirectory;
                if (string.IsNullOrWhiteSpace(root))
                    return CommandResult.Fail("当前没有可打开的运行时模块目录");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"")
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开运行时模块目录: {root}");
            }),
        }, source);
    }

    private static bool IsLocalMutationSource(string source)
        => source.Equals("UI", StringComparison.OrdinalIgnoreCase)
           || source.Equals("手动", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("host:", StringComparison.OrdinalIgnoreCase);
}
