using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.ServiceHost;

/// <summary>Composes the host command bus, settings, module lifecycle and local development services.</summary>
internal static partial class ServiceComposer
{
    /// <summary>服务随登录自启动的设置键。</summary>
    public const string ServiceAutostartSettingKey = "svc.autostart";

    /// <summary>
    /// 无头导出命令手册（<c>--export-command-manual &lt;路径&gt;</c>）。
    ///
    /// 复用后台装配：手册的价值在于它是**运行时注册表的忠实投影**，因此必须走宿主真正
    /// 使用的那条装载路径——同一套模块发现、同一套命令注册。
    /// 另起一套轻量装配会得到一份"看起来对"但与实际不符的手册，那比没有手册更糟。
    ///
    /// 本入口是冻结后的唯一手册生成入口，供发布管线在模块部署后从宿主进程外刷新。
    /// </summary>
    public static int ExportCommandManual(string outputPath, Assembly identityAssembly)
    {
        ServiceComposition? composition = null;
        try
        {
            var executable = Environment.ProcessPath ?? identityAssembly.Location;
            composition = Build(executable, identityAssembly);

            // 导出是一次性的离线组合，不是服务：模块据 RunMode 决定不开端口、不写对外通告（6.0.0，DEC-071）。
            if (composition.Modules != null)
                composition.Modules.RunMode = HistoryVulcan.Core.Modules.HostRunMode.OfflineCli;

            // 使用运行时注册表导出；模块自身的启动行为由模块合同约束。
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
        => Build(executablePath, identityAssembly, probe: null);

    /// <summary>测试装载（<c>--probe</c>）用的运行区与数据根：都在临时目录，不碰正式运行区。</summary>
    internal sealed record ProbeRoots(string ModulesDirectory, string ModuleDataRoot);

    internal static ServiceComposition Build(string executablePath, Assembly identityAssembly, ProbeRoots? probe)
    {
        AppIdentity.Use(identityAssembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var servicePaths = new AppPaths(identity.Name, Path.Combine(paths.Root, "service"), createModulesDirectory: false);
        var log = new ShellLog(servicePaths);
        var settings = new SettingsService(servicePaths);
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);

        // 宿主总线交给模块之前封口：确认、界面线程与远端路由只由宿主装配，前端经 RegisterFrontend 登记。
        bus.SealHostWiring();
        var modules = new ModuleHost(
            new RuntimeModuleDiscoverySource(probe?.ModulesDirectory ?? paths.ModulesDir), log)
        {
            // 5.9.0（DEC-070）：模块数据目录独立于包槽位，装包、热重载、卸载都不动它。
            ModuleDataRoot = probe?.ModuleDataRoot ?? Path.Combine(paths.Root, ModuleDataDirectoryName),
            RunMode = probe != null ? HistoryVulcan.Core.Modules.HostRunMode.Probe : HistoryVulcan.Core.Modules.HostRunMode.Service,
        };
        modules.Attach(registry, bus);
        RegisterServiceModuleCommands(registry, modules);
        RegisterSettingCommands(registry, settings);

        // 开发恢复入口由宿主提供，不依赖业务模块装载成功。
        var development = HistoryVulcan.Services.Development.DevelopmentCommands.Register(
            registry, bus, settings, paths.Root);

        HistoryVulcan.Services.Commands.CommandCatalogCommands.RegisterAll(
            registry,
            source: "framework:service");

        var hostEvents = new HistoryVulcan.Services.Modules.HostEventPublisher(registry, bus, log, modules);
        var composition = new ServiceComposition
        {
            ServiceName = identity.Name + ".Backend",
            Registry = registry,
            Bus = bus,
            Settings = settings,
            Log = log,
            Modules = modules,
            Development = development,
            // 与前端此前的 DataDirectory 同值：脚本相对路径基准不变。
            DataDirectory = paths.Root,
            RegisterAutostartOnFirstRun = true,
            Autostart = new WindowsRunAutostartManager(),
            HostEvents = hostEvents,
        };
        RegisterHostInfoCommand(registry, composition, executablePath, settings);
        RegisterLogRecentCommand(registry, log);

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
                new AppPaths(identity.Name, Path.Combine(paths.Root, "service"), createModulesDirectory: false));
            var manager = new WindowsRunAutostartManager();
            var enabled = settings.Get(ServiceAutostartSettingKey) is not { } value
                          || !bool.TryParse(value, out var parsed)
                          || parsed;
            var serviceName = identity.Name + ".Backend";
            manager.SetEnabled(serviceName, executablePath, ["--service"], enabled);
            Console.WriteLine($"HistoryVulcan 登录启动已{(enabled ? "修复" : "关闭")}: {serviceName}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"修复登录启动失败: {ex.Message}");
            return 1;
        }
    }

    internal static void RegisterSettingCommands(
        CommandRegistry registry,
        ISettingsService settings)
    {
        registry.Register(BuiltinCommandDefinitions.Bind(
            "vulcan.app.set",
            CommandDescriptor.Sync(context =>
            {
                var key = context.RequireString("key");
                if (string.IsNullOrWhiteSpace(key))
                    return CommandResult.Fail("配置键不能为空。");

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
                    var value = settings.Get(key);
                    return value == null
                        ? CommandResult.Ok($"{key} (未设置)")
                        : CommandResult.Ok($"{key} = {DisplayServiceSettingValue(key, value)}");
                }

                var values = settings.All();
                return values.Count == 0
                    ? CommandResult.Ok("(无配置项)")
                    : CommandResult.Ok(
                        $"共 {values.Count} 项:" + string.Concat(values.Select(item =>
                            $"\n  {item.Key} = {DisplayServiceSettingValue(item.Key, item.Value)}")));
            })),
            "framework:service");
    }

    private static string DisplayServiceSettingValue(string key, string value)
        => SensitiveName.IsSensitive(key) ? "(已配置)" : value;

    /// <summary>模块数据目录根的名字：<c>%AppData%\HistoryVulcan\ModuleData\&lt;模块名&gt;</c>（5.9.0，DEC-070）。</summary>
    internal const string ModuleDataDirectoryName = "ModuleData";

    /// <summary>
    /// <c>vulcan.host.info</c>（5.9.0，DEC-070）：宿主的版本、运行方式与各个根目录。
    /// 模块以前按进程名找宿主可执行文件、按固定路径猜 z-Publish、直接读宿主设置文件，现在一律问这一条。
    /// </summary>
    private static void RegisterHostInfoCommand(
        CommandRegistry registry, ServiceComposition composition, string executablePath, ISettingsService settings)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.host.info",
            Domain = "vulcan",
            CommandClass = "host",
            Summary = "宿主版本、运行方式，以及项目库、工作区、模块运行区、模块数据目录与宿主可执行文件的位置",
            Example = "vulcan.host.info",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                var modules = composition.Modules;
                var info = new
                {
                    version = AppIdentity.Current.Version,
                    runMode = (modules?.RunMode ?? HistoryVulcan.Core.Modules.HostRunMode.Service).ToString(),
                    libraryRoot = HistoryVulcan.Services.Development.ProjectLibraryRoot.Resolve(settings),
                    worktreeRoot = HistoryVulcan.Services.Development.WorktreeCommands.ResolveRoot(settings, null),
                    modulesRoot = modules?.ModulesDirectory ?? "",
                    moduleDataRoot = modules?.ModuleDataRoot ?? "",
                    dataRoot = composition.DataDirectory ?? "",
                    // 服务与命令行同目录发布；从 Cli 进程问时也报正式服务程序，Mercury 据此拉起宿主。
                    hostExecutable = Path.Combine(
                        Path.GetDirectoryName(executablePath) ?? "", AppIdentity.Current.Name + ".exe"),
                };
                return CommandResult.Ok(
                    $"HistoryVulcan {info.version}（{info.runMode}）\n"
                    + $"项目库: {info.libraryRoot}\n工作区根: {info.worktreeRoot}\n"
                    + $"模块运行区: {info.modulesRoot}\n模块数据: {info.moduleDataRoot}\n"
                    + $"宿主数据: {info.dataRoot}\n宿主可执行文件: {info.hostExecutable}",
                    BusJson.ToElement(info));
            }),
        }, "framework:service");
    }

    /// <summary>
    /// <c>vulcan.log.recent</c>（6.0.0，DEC-071）：宿主日志缓冲里最近的若干条。
    /// 控制台装上时补历史用；此后的新纪录订阅 <c>vulcan.log.entry</c>。形状与该事件的载荷相同。
    /// </summary>
    internal static void RegisterLogRecentCommand(CommandRegistry registry, IShellLog log)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.log.recent",
            HiddenReason = "前端补控制台历史用，经总线调用；AI 读日志请用 diana.log.read。",
            Domain = "vulcan",
            CommandClass = "log",
            Summary = "宿主日志缓冲里最近的若干条（与 vulcan.log.entry 事件同形），供控制台补历史",
            Example = "vulcan.log.recent count=500",
            Readonly = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "最多返回多少条（取最新的）",
                    Type = ParamType.Int,
                    Default = "500",
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var count = Math.Max(0, ctx.GetInt("count", 500));
                var snapshot = log.Snapshot();
                var entries = snapshot
                    .Skip(Math.Max(0, snapshot.Count - count))
                    .Where(entry => !entry.Category.Equals(BusEventHub.LogCategory, StringComparison.Ordinal))
                    .Select(HostEventPublisher.ToPayload)
                    .ToList();
                return CommandResult.Ok($"最近 {entries.Count} 条日志", BusJson.ToElement(entries));
            }),
        }, "framework:service");
    }

    private static void RegisterServiceModuleCommands(
        CommandRegistry registry,
        ModuleHost host)
    {
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
                        ? "当前无已加载模块。请检查运行区目录（vulcan.module.open）与发现诊断。"
                        : string.Join('\n', host.Modules.Select(module =>
                            module.Attached
                                ? $"{module.ModuleName} {module.Version} ({module.CommandCount} 条指令)"
                                : $"{module.ModuleName} {module.Version} ✗ 未接上宿主，指令未注册"
                                  + Environment.NewLine + "    "
                                  + string.Join(Environment.NewLine + "    ", module.AttachFailures))),
                    // 6.0.0（DEC-071）：交 JSON，不交宿主内部的 ModuleMeta。
                    BusJson.ToElement(host.Modules))),
        }, "framework:service");

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.ready",
            Domain = "vulcan",
            CommandClass = "module",
            Summary = "查询本轮模块装载是否已经全部接上宿主",
            Example = "vulcan.module.ready",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                // 装载中看到的指令目录是不完整的。5.1.2 之前没有任何办法问出这件事，
                // 于是消费方只能靠「等一会儿再拉一次」猜——猜错的那次就是少几个模块。
                var attached = host.Modules.Count(module => module.Attached);
                if (!host.IsReady)
                {
                    return CommandResult.Ok(
                        $"装载中：已接上 {attached} 个模块，目录尚不完整。", false);
                }

                var pending = host.Modules.Where(module => !module.Attached).ToList();
                return CommandResult.Ok(
                    pending.Count == 0
                        ? $"就绪：{attached} 个模块全部接上宿主。"
                        : $"就绪：{attached} 个模块接上宿主；未接上 "
                          + string.Join("、", pending.Select(module => module.ModuleName)),
                    true);
            }),
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
            Summary = "卸载模块并从 AppData 运行区删除完整模块包；数据目录默认保留，purge=true 一并删除",
            Example = "vulcan.module.uninstall name=HistoryJanus",
            Level = CommandLevel.Ask,
            Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ui.button"] = "true",
                ["ui.button.label"] = "卸载模块",
                ["ui.button.command"] = "vulcan.module.uninstall",
                ["ui.button.confirm"] = "确认卸载模块并删除其运行包？",
            },
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "vulcan.module.list 中的模块名",
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "purge",
                    Description = "同时删除该模块的数据目录（ModuleData\\<模块名>）；默认保留",
                    Type = ParamType.Bool,
                    Default = "false",
                    AllowedValues = ["true", "false"],
                },
            ],
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块卸载只允许认证的本机宿主通道。");
                var target = ctx.RequireString("name");
                var purge = ctx.GetBool("purge");
                return await Task.Run(() => host.Uninstall(target, purge), ctx.Cancellation)
                    .ConfigureAwait(false);
            },
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

    /// <summary>
    /// 运行包变更是否来自本机可信通道。
    /// </summary>
    /// <remarks>
    /// 6.0.0（DEC-071）起模块调用由宿主盖章 <c>module:&lt;名&gt;[:&lt;内层&gt;]</c>：没有内层即模块自己发起，可信；
    /// 有内层就按最里面那一层判——网关转进来的远端请求因此不会因为外面套了模块章就变成可信。
    /// 5.9.0 的 <c>diana.</c> 前缀兼容已删除。
    /// </remarks>
    internal static bool IsLocalModuleMutationSource(string source)
    {
        var value = source ?? "";
        if (value.StartsWith(ModuleSource.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            var inner = ModuleSource.Innermost(value);
            if (inner.Length == 0)
                return true;
            value = inner;
        }

        return value.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
               || value.Equals("UI", StringComparison.OrdinalIgnoreCase)
               || value.Equals("手动", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("host:", StringComparison.OrdinalIgnoreCase)
               || value.Equals("cli:runtime", StringComparison.OrdinalIgnoreCase)
               || value.Equals("cli:local", StringComparison.OrdinalIgnoreCase);
    }
}
