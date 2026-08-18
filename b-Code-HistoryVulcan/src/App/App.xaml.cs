using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Web;
using HistoryVulcan.Shell;

namespace HistoryVulcan.App;

/// <summary>
/// HistoryVulcan 独立演示宿主(§9):用于验证框架脱离 Janus 仍可构建和运行。
/// M2:控制台窗口由 Shell 提供真实实现;本层注册自定义指令示范
/// (vulcan.log.flood,兼作验收 8 的承压测试入口)。
/// 控制面板与资源窗口由 Shell 提供，派生应用可继续注册自己的业务窗口。
/// </summary>
public partial class App : Application
{
    private ShellLog? _log;
    private ShellServiceClient? _serviceClient;
    private Mutex? _frontendMutex;

    private const string FrontendMutexName = "Local\\OneHistory.HistoryVulcan.Frontend";

    /// <summary>诊断指令开关(DEC-023):默认关闭,正式命令集不含承压注水等诊断工具。</summary>
    private const string DiagnosticCommandsSettingKey = "diagnostics.commands";
    internal const string ServiceAutostartSettingKey = "svc.autostart";

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _frontendMutex = new Mutex(initiallyOwned: true, FrontendMutexName, out var created);
        if (!created)
        {
            ActivateExistingFrontend();
            Shutdown();
            return;
        }

        AppIdentity.Use(typeof(App).Assembly);
        var identity = AppIdentity.Current;
        var paths = new AppPaths(identity.Name);
        var log = new ShellLog(paths);
        var settings = new SettingsService(paths);
        _log = log;
        RemoveLegacyDemoPanel(paths, log);

        // N-05:全局未处理异常捕获 → 落日志并由 Shell 自动打开控制台,不弹错误框
        DispatcherUnhandledException += (_, args) =>
        {
            // AvalonDock can raise a stale visual-tree mouse-leave exception while
            // a pane template is replaced during focus/maximize. It is harmless
            // after the layout has been rebuilt, but must not look like an HistoryVulcan
            // fatal error in the console.
            if (IsTransientAvalonDockMouseLeave(args.Exception))
            {
                log.Log(ShellLogLevel.Debug, "shell.chrome", "Ignored transient AvalonDock mouse-leave exception during pane rebuild");
                args.Handled = true;
                return;
            }

            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Log(ShellLogLevel.Fatal, "app", $"未处理异常(非 UI 线程): {args.ExceptionObject}");

        var config = CreateStandaloneFrontendConfig(identity);
        config.ModuleDirectory = paths.ModulesDir;

        // 默认布局保留控制台底部停靠位置；业务页面由模块提供。
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = "console",
            Title = "控制台",
            DefaultSide = DockSide.Bottom,
            DefaultRatio = 0.28,
            // 控制台内容由 Shell 提供(§4.4);此处只声明停靠位置
        });
        config.ToolWindows.Add(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Modules,
            Title = "模块管理",
            // 左侧：右侧默认不再放页面。宽度与其他左侧页取同一个 0.38，
            // 否则左栏宽度会取决于哪个模块最后装载。
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.38,
            // 内容由 Shell 的 ModulesView 接管；这里只声明独立宿主的默认位置。
        });
        // 承压注水是诊断工具,不属于正式命令集(DEC-023):默认不注册,
        // 只有显式把 diagnostics.commands 置为 true 的宿主才登记。
        // 它同时是异步长任务 + Progress 上报的示范(§5.2)与验收 8 / N-03 的承压入口,
        // 因此保留能力而不是删除。
        if (bool.TryParse(settings.Get(DiagnosticCommandsSettingKey), out var diagnostics) && diagnostics)
        {
            config.ConfigureCommands = registry =>
            {
                registry.Register(BuildLogFloodCommand(log));
            };
            log.Warn("app", $"诊断指令已启用({DiagnosticCommandsSettingKey}=true):vulcan.log.flood 已注册。");
        }

        var window = new ShellWindow(config, new FileLayoutStore(paths), log, settings, paths.Root);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var executablePath = ResolveExecutablePath();
        var endpointPath = Path.Combine(paths.Root, "service", "endpoint.json");
        var service = ConnectToService(endpointPath, executablePath, log);
        if (service != null)
        {
            _serviceClient = service;
            service.LogReceived += (_, entry) => window.AddTransientLog(entry);
            service.ModuleRevisionReceived += revision =>
            {
                _ = ReloadUiModulesFromServiceAsync(service, window.Modules, log);
            };
            window.Commands.RemoteExecutor = service.ExecuteAsync;
            window.Commands.ShouldUseRemoteCommand = (text, source) =>
            {
                if (source.Equals("Service:Relay", StringComparison.OrdinalIgnoreCase))
                    return false;

                // 本机已登记且需 UI 线程的页面状态命令（如 HistoryMinerva.convert）就地执行，
                // 勿转到服务进程——那边没有页面实例。
                try
                {
                    var parsed = CommandParser.Parse(text.Trim());
                    if (IsBackendMcpSettingCommand(parsed))
                        return true;
                    if (parsed.Name.StartsWith("vulcan.app.", StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (window.Commands.Registry.TryGet(parsed.Name, out var descriptor)
                        && descriptor.RequiresUiThread
                        && descriptor.ExecutionSite != CommandExecutionSite.Frontend)
                        return false;
                }
                catch (CommandSyntaxException)
                {
                }

                return true;
            };
            _ = service.RunEventLoopAsync(window.Commands);
            _ = ReloadUiModulesFromServiceAsync(service, window.Modules, log);
        }
        window.Show();

        log.Info("app", $"{identity.Name} {identity.Version} 启动完成,数据目录: {paths.Root}");

        // --exec "指令":启动后顺序执行(自动化/自测入口)
        var startupCommands = new List<string>();
        for (var i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] != "--exec")
                continue;
            if (i + 1 >= e.Args.Length)
            {
                log.Warn("app", "启动参数 --exec 缺少后续指令，已忽略");
                break;
            }
            startupCommands.Add(e.Args[++i]);
        }

        if (e.Args.Any(arg => arg.Equals("--focus-console", StringComparison.OrdinalIgnoreCase)))
        {
            startupCommands.Add("vulcan.app.focusconsole");
        }

        if (startupCommands.Count > 0)
            _ = RunStartupCommandsAsync(window, startupCommands);
    }

    private static async Task RunStartupCommandsAsync(ShellWindow window, List<string> commands)
    {
        foreach (var command in commands)
            await window.Commands.ExecuteAsync(command, "脚本:startup");
    }

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
    internal static int ExportCommandManual(string outputPath)
    {
        ServiceComposition? composition = null;
        try
        {
            var executable = Environment.ProcessPath ?? typeof(App).Assembly.Location;
            composition = BuildServiceComposition(executable);

            // 只装载模块，不启动 Web / MCP 监听：导出不需要对外服务，
            // 顺带避免与正在运行的后台服务抢端口。
            composition.Modules?.Start();

            var markdown = HistoryVulcan.Extensibility.Mcp.CommandManualGenerator.Render(
                composition.Registry!,
                new HistoryVulcan.Extensibility.Mcp.CommandSchemaExporter(composition.Registry!),
                composition.Mcp?.Policy ?? "readonly");

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
                $"{HistoryVulcan.Extensibility.Mcp.CommandManualGenerator.Sha256(markdown)}");
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

    internal static ServiceComposition BuildServiceComposition(string executablePath)
    {
        AppIdentity.Use(typeof(App).Assembly);
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
            EnableUiModules = false,
        };
        var web = new WebGateway(() => bus, settings, log)
        {
            ServerId = identity.Name + ".service",
        };
        long moduleRevision = 0;
        modules.ReloadCompleted += () =>
            web.PublishModuleRevision(Interlocked.Increment(ref moduleRevision));

        modules.Attach(registry, bus, settings, servicePaths.Root);
        RegisterServiceModuleCommands(registry, modules, settings, bus);
        RegisterServiceMcpSettingCommands(registry, settings);

        // The backend registry owns both module commands and the MCP projection. Prompt and
        // audit state stay at the historical application root so moving the listener does not
        // orphan existing governance revisions or call history.
        var prompts = new Services.Mcp.PromptGovernanceStore(paths.Root, log);
        var audit = new Services.Mcp.McpAuditRecorder(paths.Root, log);
        // 4.0.0（REQ-A2）：MCP 的危险命令确认同样中继到前端。服务进程无人值守，
        // 在这里弹模态框只会阻塞到超时；前端未连接时拒绝，不放行。
        var confirmation = new ShellRelayConfirmation(() => web, log);
        Services.Mcp.McpGateway? mcp = null;
        mcp = new Services.Mcp.McpGateway(
            () => bus,
            settings,
            log,
            audit,
            prompts,
            identity,
            confirmation.ConfirmRemote);
        HistoryVulcan.Services.Mcp.McpCommands.RegisterAll(
            registry,
            () => bus,
            () => mcp,
            settings,
            prompts,
            source: "framework:service");

        return new ServiceComposition
        {
            ServiceName = identity.Name + ".Backend",
            Registry = registry,
            Bus = bus,
            Settings = settings,
            Log = log,
            Modules = modules,
            Mcp = mcp,
            Web = web,
            EndpointFile = Path.Combine(servicePaths.Root, "endpoint.json"),
            RegisterAutostartOnFirstRun = true,
            Autostart = new WindowsRunAutostartManager(),
        };
    }

    internal static int RepairAutostart(string executablePath)
    {
        try
        {
            AppIdentity.Use(typeof(App).Assembly);
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

    internal static ShellConfig CreateStandaloneFrontendConfig(
        HistoryVulcan.Core.ApplicationIdentity identity)
        => new()
        {
            AppName = identity.Name,
            AppVersion = identity.Version,
            EnableModules = false,
            EnableUiModules = true,
            // 独立双进程宿主的 MCP 由后台权威注册表统一承载；前端只保留远程管理视图。
            EnableMcp = false,
            RequireConfirmedModuleSources = true,
            EnableRemoteManagementViews = true,
            CloseBehavior = ShellCloseBehavior.Hide,
            // HistoryVulcan 独立宿主是模块生命周期的最终所有者；Janus 等产品只声明
            // 自己的业务窗口与模块，不再包装第二套 ModuleHost/ModulesView。
        };

    internal static bool IsBackendMcpSettingCommand(ParsedCommand parsed)
    {
        if (!parsed.Name.Equals("vulcan.app.get", StringComparison.OrdinalIgnoreCase)
            && !parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase))
            return false;

        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        return key?.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static void RegisterServiceMcpSettingCommands(
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

    internal static void MigrateLegacyMcpSettings(
        ISettingsService legacy,
        ISettingsService service,
        IShellLog log)
    {
        string[] keys =
        [
            Services.Mcp.McpGateway.KeyPort,
            Services.Mcp.McpGateway.KeyPolicy,
            Services.Mcp.McpGateway.KeyToken,
            Services.Mcp.McpGateway.KeyAutostart,
            Services.Mcp.McpGateway.KeyTimeout,
            Services.Mcp.McpGateway.KeyConfirm,
            Services.Mcp.McpGateway.KeyConfirmTimeout,
            Services.Mcp.McpGateway.KeyPortRetries,
            Services.Mcp.McpGateway.KeySessionLimit,
        ];
        var migrated = 0;
        foreach (var key in keys)
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
                            $"{module.ModuleName} {module.Version} ({module.CommandCount} 条指令)")),
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
            Handler = async ctx =>
            {
                var name = ctx.RequireString("name");
                string? frontendNote = null;
                if (bus.FrontendExecutor is { } frontend)
                {
                    var remote = await frontend(
                        $"vulcan.module.unload name={CommandParser.QuoteArg(name)}",
                        "framework:service",
                        ctx.Cancellation).ConfigureAwait(false);
                    frontendNote = remote.Success
                        ? "前端界面已一并卸载"
                        : $"前端: {remote.Message}";
                }

                var local = host.Unload(name);
                if (!local.Success)
                {
                    if (frontendNote == "前端界面已一并卸载")
                        return CommandResult.Ok($"前端界面已卸载，但后台: {local.Message}");
                    return local;
                }

                return frontendNote == null
                    ? local
                    : CommandResult.Ok($"{local.Message}；{frontendNote}");
            },
        }, "framework:service");

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
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块安装只允许认证的本机宿主通道。");
                return await InstallRuntimePackageAsync(
                    host,
                    bus,
                    ctx.RequireString("path"),
                    ctx.Cancellation).ConfigureAwait(false);
            },
        }, "framework:service");

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
            Handler = async ctx =>
            {
                if (!IsLocalModuleMutationSource(ctx.Source))
                    return CommandResult.Fail("模块移除只允许认证的本机宿主通道。");
                return await RemoveRuntimePackageAsync(
                    host,
                    bus,
                    ctx.RequireString("name"),
                    ctx.Cancellation).ConfigureAwait(false);
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

    private static bool IsLocalModuleMutationSource(string source)
        => source.StartsWith("Shell:", StringComparison.OrdinalIgnoreCase)
           || source.Equals("UI", StringComparison.OrdinalIgnoreCase)
           || source.Equals("手动", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("脚本:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("host:", StringComparison.OrdinalIgnoreCase)
           || source.StartsWith("diana.", StringComparison.OrdinalIgnoreCase);

    private static async Task ReloadUiModulesFromServiceAsync(
        ShellServiceClient service,
        ModuleHost? host,
        IShellLog log)
    {
        if (host == null)
            return;
        var result = await service.ExecuteAsync("vulcan.module.list", "UI", CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<ModuleMeta>? modules = result.Data switch
        {
            IReadOnlyList<ModuleMeta> typed => typed,
            JsonElement element => JsonSerializer.Deserialize<List<ModuleMeta>>(
                element.GetRawText(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }),
            _ => null,
        };
        if (!result.Success || modules == null)
        {
            log.Warn("module", "无法取得后台确认的模块来源，前端 UI 模块保持原快照");
            return;
        }

        var manifests = modules
            .Select(module => module.ManifestPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToList();
        await Task.Run(() => host.ReloadConfirmedSources(manifests)).ConfigureAwait(false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceClient?.Dispose();
        _log?.Dispose(); // 冲刷文件写入队列
        try { _frontendMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _frontendMutex?.Dispose();
        base.OnExit(e);
    }

    private static bool IsTransientAvalonDockMouseLeave(Exception exception)
    {
        if (exception is not NullReferenceException)
            return false;

        return exception.StackTrace?.Contains(
            "AvalonDock.Controls.AnchorablePaneTabPanel.OnMouseLeave",
            StringComparison.Ordinal) == true;
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) &&
            Path.GetExtension(processPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return processPath;
        return Path.ChangeExtension(typeof(App).Assembly.Location, ".exe");
    }

    private static ShellServiceClient? ConnectToService(
        string endpointPath,
        string executablePath,
        IShellLog log)
    {
        try
        {
            var endpoint = ReadEndpoint(endpointPath);
            if (endpoint is { Port: > 0 })
            {
                var existing = CreateServiceClient(endpoint, endpointPath);
                if (existing.WaitForReadyAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult())
                    return existing;
                existing.Dispose();
            }

            // 走到这里说明记录里的后台连不上。必须先删掉这份记录再派生新服务：
            // 服务被强杀（或崩溃）时来不及清理 endpoint.json，残留文件会让下面的等待循环
            // 第一轮就读到旧记录并立即退出，前端于是拿着旧端口和空 accessToken 去连新服务，
            // 稳定 401 后转本地模式。3.13.0 前这个缺陷是潜伏的——端口恰好相同且不校验令牌，
            // 连上了就看不出来；补上凭据校验后它变成硬失败。
            try { File.Delete(endpointPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            endpoint = null;
            var start = new ProcessStartInfo(executablePath, "--service")
            {
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            Process.Start(start);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            while (DateTime.UtcNow < deadline && !IsUsableEndpoint(endpoint = ReadEndpoint(endpointPath)))
                Thread.Sleep(100);

            if (!IsUsableEndpoint(endpoint))
            {
                log.Warn("service", "后台服务端点不可用，前端以本地模式继续运行");
                return null;
            }

            var client = CreateServiceClient(endpoint!, endpointPath);
            if (!client.WaitForReadyAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult())
            {
                client.Dispose();
                log.Warn("service", "后台服务未在期限内就绪，前端以本地模式继续运行");
                return null;
            }
            return client;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or System.ComponentModel.Win32Exception)
        {
            log.Warn("service", $"后台连接失败，前端以本地模式继续运行: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 端点必须同时给出端口和凭据；缺一都连不上 3.13.0 起的后台。
    ///
    /// 派生服务后的等待循环用它判断"记录是否已刷新"。只判 <c>null</c> 是不够的：
    /// 服务被强杀时来不及删除 endpoint.json，残留记录端口有效、accessToken 为空，
    /// 会让循环第一轮就误判为就绪。
    /// </summary>
    internal static bool IsUsableEndpoint(ServiceEndpoint? endpoint)
        => endpoint is { Port: > 0 } && !string.IsNullOrEmpty(endpoint.AccessToken);

    private static ShellServiceClient CreateServiceClient(ServiceEndpoint endpoint, string endpointPath)
    {
        // 凭据每次调用时从 endpoint.json 现取，不捕获快照：后台重启会换发新令牌并重写该文件，
        // 而客户端的重连是长期存活的。捕获初次读到的值会让前端在后台重启后静默 401。
        // 文件读不到时回退到初次值，避免瞬时 IO 抖动把一个本来有效的会话打掉。
        var initial = endpoint.AccessToken;
        var profile = new ShellEndpointProfile(
            new Uri($"http://127.0.0.1:{endpoint.Port}/"),
            Guid.NewGuid().ToString("N"),
            AccessTokenProvider: () => ReadEndpoint(endpointPath)?.AccessToken ?? initial,
            ServerId: endpoint.ServerId,
            ConnectTimeout: TimeSpan.FromSeconds(5));
        return new ShellServiceClient(profile, "HistoryVulcan.Frontend");
    }

    private static ServiceEndpoint? ReadEndpoint(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<ServiceEndpoint>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void ActivateExistingFrontend()
    {
        try
        {
            var current = Environment.ProcessId;
            foreach (var process in Process.GetProcessesByName("HistoryVulcan"))
            {
                try
                {
                    if (process.Id == current || process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    ShowWindowAsync(process.MainWindowHandle, 9);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // A duplicate launch must never surface a dialog or crash the existing frontend.
        }
    }

    internal sealed record ServiceEndpoint(
        int Port,
        string ServerId,
        int ProcessId,
        string? AccessToken = null);

    private static void RemoveLegacyDemoPanel(AppPaths paths, IShellLog log)
    {
        var legacyPanel = Path.Combine(paths.PanelsDir, "motor.json");
        try
        {
            if (!File.Exists(legacyPanel))
                return;
            File.Delete(legacyPanel);
            log.Info("migration", $"已删除旧版演示电机面板: {legacyPanel}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn("migration", $"删除旧版演示电机面板失败: {ex.Message}");
        }
    }

    /// <summary>
    /// vulcan.log.flood:按指定速率注入日志(验收 8 / N-03 承压验证)。
    /// 异步长任务示范:后台线程产出、经 Progress 上报进度、全程不阻塞 UI(§5.2)。
    /// </summary>
    private static CommandDescriptor BuildLogFloodCommand(ShellLog log) => new()
    {
        Name = "vulcan.log.flood",
        Domain = "vulcan",
        CommandClass = "log",
        Summary = "诊断:日志承压测试,按指定速率注入日志",
        Example = "vulcan.log.flood rate=1000 seconds=30",
        // 最高 100000 条/秒 × 600 秒;误触会淹没控制台与日志文件,故走确认闸口。
        // MCP/Web 侧另由 McpExposurePolicy 硬排除,远程不可达。
        Dangerous = true,
        ConfirmPrompt = ctx =>
            $"确认注入日志 {ctx.GetInt("rate", 1000)} 条/秒 × {ctx.GetInt("seconds", 30)} 秒?",
        Parameters =
        [
            new ParameterSpec
            {
                Name = "rate",
                Description = "每秒注入条数",
                Type = ParamType.Int,
                Default = "1000",
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "seconds",
                Description = "持续秒数",
                Type = ParamType.Int,
                Default = "30",
                Position = 1,
            },
        ],
        Handler = async ctx =>
        {
            var rate = Math.Clamp(ctx.GetInt("rate", 1000), 1, 100_000);
            var seconds = Math.Clamp(ctx.GetInt("seconds", 30), 1, 600);
            var total = 0L;
            var sw = Stopwatch.StartNew();

            await Task.Run(async () =>
            {
                // 每 100ms 一批;按“目标累计 = 速率 × 已流逝时间”补齐,睡眠误差不累积
                var lastProgress = 0L;
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    var target = Math.Min(
                        (long)(sw.Elapsed.TotalSeconds * rate),
                        (long)rate * seconds);
                    while (total < target)
                    {
                        total++;
                        log.Log(ShellLogLevel.Debug, "flood",
                            $"承压测试消息 #{total} @{sw.ElapsedMilliseconds}ms");
                    }

                    if (sw.ElapsedMilliseconds - lastProgress >= 5000)
                    {
                        lastProgress = sw.ElapsedMilliseconds;
                        ctx.Progress?.Report($"{sw.Elapsed.TotalSeconds:0}s / {seconds}s,已注入 {total} 条");
                    }

                    await Task.Delay(100);
                }

                // 补齐尾差
                for (var expected = (long)rate * seconds; total < expected; total++)
                {
                    log.Log(ShellLogLevel.Debug, "flood",
                        $"承压测试消息 #{total + 1} @{sw.ElapsedMilliseconds}ms");
                }
            });

            return CommandResult.Ok(
                $"承压完成:{total} 条 / {sw.Elapsed.TotalSeconds:0.0}s,实际速率 {total / sw.Elapsed.TotalSeconds:0} 条/秒");
        },
    };

    // ---------------------------------------------------------------- 演示面板与指令(M4)

}
