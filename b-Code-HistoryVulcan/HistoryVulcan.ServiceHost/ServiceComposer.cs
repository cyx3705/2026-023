using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 无头服务的组合根（4.0.0，REQ-A3）。
///
/// 这些成员此前住在 <c>App.xaml.cs</c> 里——一个 WPF <c>Application</c> 派生类——
/// 于是"装配后台服务"和"启动前端窗口"共用同一个类型，服务进程被迫加载 WPF 程序集。
/// 迁到 ServiceHost 后，前端只剩下"连上后台"这一条依赖，A4 可以把服务做成独立 exe。
///
/// 身份程序集由调用方显式传入：<c>AppIdentity</c> 决定数据根目录名、端口派生和服务名，
/// 原实现取 <c>typeof(App).Assembly</c>，那在无头进程里不存在。
/// </summary>
public static partial class ServiceComposer
{
    /// <summary>服务随登录自启动的设置键。</summary>
    public const string ServiceAutostartSettingKey = "svc.autostart";

    /// <summary>
    /// 无头导出命令手册（<c>--export-command-manual &lt;路径&gt;</c>）。
    ///
    /// 复用后台装配：手册的价值在于它是**运行时注册表的忠实投影**，因此必须走宿主真正
    /// 使用的那条装载路径——同一套模块发现、同一套命令注册、同一套 MCP 暴露策略。
    /// 另起一套轻量装配会得到一份"看起来对"但与实际不符的手册，那比没有手册更糟。
    ///
    /// 与 <c>vulcan.command.manual</c> 的分工：那条命令带本地二次确认，供人在控制台按需
    /// 生成；本入口无人值守，供发布管线在每次模块部署后刷新。两者调用同一个生成器。
    /// </summary>
    public static int ExportCommandManual(string outputPath, Assembly identityAssembly)
    {
        ServiceComposition? composition = null;
        try
        {
            var executable = Environment.ProcessPath ?? identityAssembly.Location;
            composition = Build(executable, identityAssembly);

            // 只装载模块，不启动 Web / MCP 监听：导出不需要对外服务，
            // 顺带避免与正在运行的后台服务抢端口。
            composition.Modules?.Start();

            var markdown = HistoryVulcan.Services.Commands.CommandCatalogCommands.RenderManual(
                composition.Registry!);

            var target = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // 原子写入：先写临时文件再替换，避免管线中断留下半份手册。
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, markdown, new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);

            var count = composition.Registry!.All().Count;
            Console.WriteLine(
                $"命令手册已导出: {count} 条命令，SHA-256 " +
                $"{HistoryVulcan.Services.Commands.CommandCatalogCommands.Sha256(markdown)}");
            Console.WriteLine(target);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"导出命令手册失败: {ex.Message}");
            return 1;
        }
        finally
        {
            composition?.Dispose();
        }
    }

    public static ServiceComposition Build(string executablePath, Assembly identityAssembly)
    {
        AppIdentity.Use(identityAssembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var servicePaths = new AppPaths(identity.Name, Path.Combine(paths.Root, "service"));
        var log = new ShellLog(servicePaths);
        var settings = new SettingsService(servicePaths);
        MigrateLegacyMcpSettings(new SettingsService(paths), settings, log);
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        // 全局快捷键不再由宿主装配。此前这里要先用 Assembly.LoadFrom 全盘预扫描模块 DLL
        // （不进 ALC、不可卸载）找出 IGlobalShortcutHost 实现，再按约定构造签名反射构造——
        // 为一个「按键 → 命令名」的能力付出了预扫描 + 类型契约 + 扫描驱动三重成本。
        // 现由提供方自持并经 <域>.hotkey.* 命令暴露，宿主不需要知道快捷键这个概念存在。
        var modules = new ModuleHost(
            new RuntimeModuleDiscoverySource(paths.ModulesDir), log)
        {
            EnableCommands = true,
        };
        modules.Attach(registry, bus, settings, servicePaths.Root);
        RegisterServiceModuleCommands(registry, modules, settings, bus);
        RegisterServiceMcpSettingCommands(registry, settings);

        // 指令自省面（vulcan.command.list / show / domains / manual）随宿主装配，
        // 目录与手册只读注册表。远端暴露由指令自己的 HiddenReason / Level / Readonly 声明，
        // 宿主不再另做一层 MCP 投影或策略锁。
        // 模块开发路线（4.6.0 从 HistoryDiana 迁入）：工作区、发布、装机。
        //
        // 它此前住在模块里，于是每一轮模块开发都依赖那个模块装载成功——而它自己也要
        // 走这条路线来改。放在宿主则相反：只要宿主活着，任何一个模块坏掉都能被单独修好。
        HistoryVulcan.Services.Development.DevelopmentCommands.RegisterAll(
            registry, bus, settings, paths.Root);

        HistoryVulcan.Services.Commands.CommandCatalogCommands.RegisterAll(
            registry,
            source: "framework:service");

        var composition = new ServiceComposition
        {
            ServiceName = identity.Name + ".Backend",
            Registry = registry,
            Bus = bus,
            Settings = settings,
            Log = log,
            Modules = modules,
            // 与前端此前的 DataDirectory 同值：脚本相对路径基准不变（REQ-A6）。
            DataDirectory = paths.Root,
            RegisterAutostartOnFirstRun = true,
            Autostart = new WindowsRunAutostartManager(),
        };

        // 服务指令（vulcan.svc.* / vulcan.app.*）在这里注册而不是在 Run 里（4.5.0）。
        //
        // 它们此前跟着 Run 走，于是任何不跑循环的入口都看不到它们——`--cli` 里
        // vulcan 域只剩一半，而「查服务状态」恰恰是命令行最常问的事。
        // 真正只有循环才做得到的是「停机」一件，那一件经 composition.RequestStop 接进来，
        // 未接上时相关指令明确失败，而不是假装停了一个不存在的服务。
        ServiceCommands.RegisterAll(
            registry,
            composition,
            () => composition.RequestStop?.Invoke(),
            executablePath,
            serviceArguments: [HostArgumentParser.LegacyServiceSwitch]);

        return composition;
    }

    public static int RepairAutostart(string executablePath, Assembly identityAssembly)
    {
        try
        {
            AppIdentity.Use(identityAssembly);
            var identity = AppIdentity.Current;
            var paths = new AppPaths(identity.Name);
            var settings = new SettingsService(
                new AppPaths(identity.Name, Path.Combine(paths.Root, "service")));
            var manager = new WindowsRunAutostartManager();
            var enabled = settings.Get(ServiceAutostartSettingKey) is not { } value
                          || !bool.TryParse(value, out var parsed)
                          || parsed;
            var serviceName = identity.Name + ".Backend";
            manager.SetEnabled(serviceName, executablePath, ["--service"], enabled);
            RemoveLegacyAutostartAlias();
            Console.WriteLine($"HistoryVulcan 登录启动已{(enabled ? "修复" : "关闭")}: {serviceName}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"修复登录启动失败: {ex.Message}");
            return 1;
        }
    }

    private static void RemoveLegacyAutostartAlias()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue("AppShell.Backend", throwOnMissingValue: false);
    }

    public static void RegisterServiceMcpSettingCommands(
        CommandRegistry registry,
        ISettingsService settings)
    {
        registry.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.set",
            CommandDescriptor.Sync(context =>
            {
                var key = context.RequireString("key");
                if (!key.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("后台设置入口只接受 mcp.* 配置键");

                var value = context.RequireString("value");
                settings.Set(key, value);
                return CommandResult.Ok($"{key} = {DisplayServiceSettingValue(key, value)}");
            })),
            "framework:service");
        registry.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.get",
            CommandDescriptor.Sync(context =>
            {
                var key = context.GetString("key");
                if (key != null)
                {
                    if (!key.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
                        return CommandResult.Fail("后台设置入口只接受 mcp.* 配置键");
                    var value = settings.Get(key);
                    return value == null
                        ? CommandResult.Ok($"{key} (未设置)")
                        : CommandResult.Ok($"{key} = {DisplayServiceSettingValue(key, value)}");
                }

                var values = settings.All()
                    .Where(item => item.Key.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                return values.Count == 0
                    ? CommandResult.Ok("(无 MCP 配置项)")
                    : CommandResult.Ok(
                        $"共 {values.Count} 项:" + string.Concat(values.Select(item =>
                            $"\n  {item.Key} = {DisplayServiceSettingValue(item.Key, item.Value)}")));
            })),
            "framework:service");
    }

    private static string DisplayServiceSettingValue(string key, string value)
    {
        var normalized = key.Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
            ? "(已配置)"
            : value;
    }

    private static readonly string[] LegacyMcpSettingKeys =
    [
        "mcp.port",
        "mcp.policy",
        "mcp.token",
        "mcp.autostart",
        "mcp.timeout",
        "mcp.confirm",
        "mcp.confirmtimeout",
        "mcp.portretries",
        "mcp.sessionlimit",
    ];

    public static void MigrateLegacyMcpSettings(
        ISettingsService legacy,
        ISettingsService service,
        IShellLog log)
    {
        var migrated = 0;
        foreach (var key in LegacyMcpSettingKeys)
        {
            if (service.Get(key) != null || legacy.Get(key) is not { } value)
                continue;
            service.Set(key, value);
            migrated++;
        }

        if (migrated > 0)
            log.Info("mcp", $"已迁移 {migrated} 项旧前端 MCP 配置到后台设置");
    }

    private static void RegisterServiceModuleCommands(
        CommandRegistry registry,
        ModuleHost host,
        SettingsService settings,
        CommandBus bus)
    {
        _ = settings;
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.list",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "列出已加载模块",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
                CommandResult.Ok(
                    host.Modules.Count == 0
                        ? "当前无已加载模块。请检查 vulcan.module.roots 与发现诊断。"
                        : string.Join('\n', host.Modules.Select(module =>
                            module.Attached
                                ? $"{module.ModuleName} {module.Version} ({module.CommandCount} 条指令)"
                                : $"{module.ModuleName} {module.Version} ✗ 未接上宿主，指令未注册"
                                  + Environment.NewLine + "    "
                                  + string.Join(Environment.NewLine + "    ", module.AttachFailures))),
                    host.Modules)),
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.reload",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "重载全部后台模块",
            Handler = async _ =>
            {
                await Task.Run(host.Reload).ConfigureAwait(false);
                return CommandResult.Ok($"重载完成: {host.Modules.Count} 个模块");
            },
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.unload",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "卸载一个已装载模块（命令与界面）；不改磁盘，reload 会装回",
            Example = "vulcan.module.unload name=HistoryJanus",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "vulcan.module.list 中的模块名",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                // 界面与后台在同一进程、同一张注册表里，卸载只有这一步。
                // 双进程时代这里还要先中继到界面卸掉同名快照以释放文件锁，
                // 那条中继在进程内会打回本命令上无限递归，已随进程外前端一并删除。
                return host.Unload(ctx.RequireString("name"));
            }),
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.install",
            HiddenReason = "运行包变更只允许认证的本机宿主通道",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "从已校验候选包原子安装并重载运行时模块",
            Example = "vulcan.module.install path=C:\\candidate\\HistoryJanus",
            Parameters = [new ParameterSpec
            {
                Name = "path",
                Description = "含 module.manifest.json 与完整 SHA256SUMS 的绝对包目录",
                Required = true,
                Position = 0,
            }],
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块安装只允许认证的本机宿主通道。");
                var path = ctx.RequireString("path");
                return await Task.Run(() => host.InstallPackage(path), ctx.Cancellation)
                    .ConfigureAwait(false);
            },
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.remove",
            HiddenReason = "运行包变更只允许认证的本机宿主通道",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "从运行区原子移除模块包并刷新运行快照",
            Example = "vulcan.module.remove name=HistoryJanus",
            Level = CommandLevel.Ask,
            Parameters = [new ParameterSpec
            {
                Name = "name",
                Description = "vulcan.module.list 中的模块名",
                Required = true,
                Position = 0,
            }],
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块移除只允许认证的本机宿主通道。");
                var target = ctx.RequireString("name");
                return await Task.Run(() => host.RemovePackage(target), ctx.Cancellation)
                    .ConfigureAwait(false);
            },
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.uninstall",
            HiddenReason = "运行包变更只允许认证的本机宿主通道",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "卸载模块并从 AppData 运行区删除完整模块包",
            Example = "vulcan.module.uninstall name=HistoryJanus",
            Level = CommandLevel.Ask,
            Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui.button"] = "true",
                ["ui.button.label"] = "卸载模块",
                ["ui.button.command"] = "vulcan.module.uninstall",
                ["ui.button.confirm"] = "确认卸载模块并删除其运行包？",
            },
            Parameters = [new ParameterSpec
            {
                Name = "name",
                Description = "vulcan.module.list 中的模块名",
                Required = true,
                Position = 0,
            }],
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块卸载只允许认证的本机宿主通道。");
                var target = ctx.RequireString("name");
                return await Task.Run(() => host.Uninstall(target), ctx.Cancellation)
                    .ConfigureAwait(false);
            },
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.roots",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "查看固定的后台运行时模块目录（兼容查询）",
            Parameters = [new ParameterSpec
            {
                Name = "paths",
                Description = "兼容参数；3.12.0 起拒绝修改",
                Position = 0,
            }],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var paths = ctx.GetString("paths");
                if (string.IsNullOrWhiteSpace(paths))
                    return CommandResult.Ok($"固定运行时模块目录: {host.ModulesDirectory}");
                return CommandResult.Fail(
                    $"3.12.0 起模块目录固定为 {host.ModulesDirectory}；module.roots 设置不再生效。");
            }),
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.open",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "打开固定的运行时模块目录",
            Example = "vulcan.module.open",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var root = host.ModulesDirectory;
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    return CommandResult.Fail("当前没有可打开的模块发现根");

                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"")
                {
                    UseShellExecute = true,
                });
                return CommandResult.Ok($"已打开运行时模块目录: {root}");
            }),
        }, "framework:service");
    }

    internal static bool IsLocalModuleMutationSource(string source)
        => source.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
           || source.Equals("UI", StringComparison.OrdinalIgnoreCase)
           || source.Equals("手动", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("host:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("diana.", StringComparison.OrdinalIgnoreCase)
           || source.Equals("cli:runtime", StringComparison.OrdinalIgnoreCase)
           || source.Equals("cli:local", StringComparison.OrdinalIgnoreCase);
}
