using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Extensibility.Modules;
using HistoryVulcan.Shell.CommandSurface;
using HistoryVulcan.Shell.Console;
using HistoryVulcan.Shell.Docking;
using HistoryVulcan.Shell.Themes;
using AvalonDock.Controls;
using AvalonDock.Layout;

namespace HistoryVulcan.Shell;

/// <summary>
/// 主程序窗体(Main Frame,§2)。3.1 起为自绘顶栏 + 停靠系统容器两段结构:
/// 菜单折叠进顶栏右上角的菜单按钮(UI-03),常驻菜单行与底部状态栏均已取消
/// (UI-05,原状态栏信息改由顶栏徽章、布局文本和瞬时回执承担)。
/// M2 起指令总线为一切操作的汇聚点:菜单项点击同样是发指令(S-02),
/// 控制台手输、脚本、布局手势与派生应用共用同一张指令注册表。
/// </summary>
public partial class ShellWindow : Window, IShellCommandWorkbenchHost
{
    private readonly ShellConfig _config;
    private readonly IShellLog _log;
    private readonly DockingHost _docking;
    private readonly string _dataDirectory;
    private readonly CommandBus _bus;
    private readonly CommandSelectionState _commandSelection;
    private readonly CommandHistory _history;
    private readonly DeferredCommandCatalogSession _catalogSession;
    private readonly ConsoleView _console;
    private readonly Panels.PanelManager _panels;

    // 0.4.4 反哺能力:由 Shell 自行装配,派生应用经下方只读属性取用
    private readonly Services.Modules.ModuleHost? _modules;
    private readonly Modules.ShellUiRegistrar _shellUi;
    private readonly Services.Mcp.McpGateway? _mcp;
    private readonly Services.Mcp.PromptGovernanceStore? _prompts;

    // 命令集页与指令详情页的选中联动(0.4.4 上抛):优先用派生应用经 ShellConfig 传入的实例,
    // 未传则自建。由构造函数赋值——工具窗口内容工厂在 DockingHost 构建默认布局时即被调用,
    // 派生应用那时拿不到 window,故联动实例必须由派生侧创建并传入。
    // UI-03:折叠后的菜单挂在顶栏菜单按钮上(挂上去才能继承窗体资源与样式)
    private readonly ContextMenu _menu = new();

    private int _errorCount;
    private bool _menusInitialized;

    // UI-09.1:true 表示已接管窗体非客户区;false 表示宿主改过 WindowStyle,
    // 只降级边框接管方式,顶栏四个按钮仍然保留并可用。
    private bool _customChrome;

    // Alt 单独按下(未与其他键组合)才呼出菜单,避免抢走 Alt+Tab 等组合
    private bool _altPressedAlone;

    // 主题字典副本:窗格基底样式与 3.1 卡片/专注模板都从这里取
    private ResourceDictionary? _themeResources;

    // UI-08:浅色/深色令牌整份切换(vulcan.app.theme),设置项持久化
    private readonly ISettingsService _settings;
    private string _theme = ThemeLight;

    // 右上角按钮组要在最上一排页签里占位,避免页签跑到按钮底下
    private readonly DispatcherTimer _chromeUpkeep;
    private bool _reservePending;
    private bool _closing;
    private bool _allowClose;
    private ContentControl? _chromeHost;
    private FrameworkElement? _chromeDragSurface;
    private readonly ShellTopBarCoordinator _topBar;

    public ShellWindow(
        ShellConfig config,
        ILayoutStore layoutStore,
        IShellLog log,
        ISettingsService settings,
        string dataDirectory)
    {
        InitializeComponent();

        _config = config;
        _log = log;
        _dataDirectory = dataDirectory;
        _settings = settings;
        Title = $"{config.AppName} v{config.AppVersion}";

        // UI-03:菜单按钮的弹出层;挂到按钮上才能继承窗体资源(菜单项样式)
        MenuButton.ContextMenu = _menu;

        // AvalonDock 主题字典必须先于令牌换位就位：HistoryVulcanTheme.xaml 内部合并了
        // ShellTokens.xaml(浅色)，而 SwapTokens 是把令牌追加到 MergedDictionaries 末尾、
        // 且幂等不重排。先换令牌再赋值 Theme 的话，令牌停在索引 0、主题字典排到它后面，
        // WPF 后者胜出 —— 表现为「设置里是深色，启动却是浅色，手动再切一次才对」。
        DockManager.Theme = new HistoryVulcanTheme();

        // UI-08:上次选择的主题先于任何界面成型生效,避免启动瞬间闪一下浅色
        ApplyTheme(settings.Get(ThemeSettingsKey) ?? ThemeLight, persist: false);

        StateChanged += (_, _) => ApplyWindowStateChrome();
        SourceInitialized += OnShellSourceInitialized;
        PreviewKeyDown += OnShellPreviewKeyDown;
        PreviewKeyUp += OnShellPreviewKeyUp;

        // 主窗体边界先于停靠布局恢复:布局像素尺寸相对窗体记录,
        // 窗体尺寸一致才能做到“重启后布局原样恢复”(验收 1 / 3)
        RestoreWindowBounds();

        LoadThemeResources();
        ApplyPaneStyles(chromeless: false);

        // ---- 指令核心(§5):注册表 + 总线 + 历史 + 控制台（命令集/详情由 Mercury 挂载）
        var registry = new CommandRegistry();
        _bus = new CommandBus(registry, log)
        {
            UiContext = SynchronizationContext.Current,
            Confirmation = new MessageBoxConfirmation(this),
        };
        _commandSelection = config.CommandSelection ?? new CommandSelectionState();
        _history = new CommandHistory(
            Path.Combine(dataDirectory, "history.txt"),
            settings.GetInt(ConsoleView.KeyHistory, 500));
        // 传入本地注册表:Mercury 未挂接时控制台的域/类过滤仍按本地权威目录工作(DEC-023)。
        _catalogSession = new DeferredCommandCatalogSession(registry);
        _console = new ConsoleView(
            log,
            _bus,
            _history,
            _catalogSession,
            settings.GetInt(ConsoleView.KeyBuffer, 50_000));
        TakeOverDescriptor(
            StandardWindowIds.Console,
            "控制台",
            DockSide.Bottom,
            0.25,
            () => _console);

        if (config.EnableModules || config.EnableRemoteManagementViews)
        {
            TakeOverDescriptor(StandardWindowIds.Modules, "模块管理", DockSide.Right, 0.32,
                () => new Views.ModulesView(() => _bus));
        }

        // 控制窗口群(§4.5,M4):JSON + C# 通道合并,每个面板一个可停靠窗口
        _panels = new Panels.PanelManager(
            Services.AppPaths.GetPanelsDir(dataDirectory), config.Panels, _bus, log);
        _panels.RegisterWindows(config.ToolWindows);

        _docking = new DockingHost(DockManager, config.ToolWindows, layoutStore, log, settings);
        _docking.Initialize();
        ConfigureCommandCompletionRouting(
            () => _docking.MaximizedId?.Equals(
                StandardWindowIds.Console,
                StringComparison.OrdinalIgnoreCase) == true,
            () =>
            {
                _ = ShowCommandCatalogForCompletionAsync();
            });
        _topBar = new ShellTopBarCoordinator(
            this,
            DockManager,
            _docking,
            _bus,
            _log,
            (RoutedCommand)Resources["Shell.Command.PageAction"]);
        // 按钮组占位与浮动窗口主题需要在「布局稳定之后」才算得准,但不能挂
        // LayoutUpdated:那个事件每帧都发,回调里任何写操作都会再触发一次布局,
        // 直接转成 100% CPU 的死循环(实测)。改为低频巡检 + 幂等写入。
        _chromeUpkeep = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _chromeUpkeep.Tick += (_, _) =>
        {
            if (_closing)
                return;
            AttachChromeBarToMainDocumentPane();
            ReserveSpaceForChromeBar();
            ApplyThemeToFloatingWindows();
        };
        Loaded += (_, _) => _chromeUpkeep.Start();
        _shellUi = new Modules.ShellUiRegistrar(_docking, Dispatcher, log);
        _docking.WindowsChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                BuildMenus();
            }
            catch (Exception ex)
            {
                _log.Error("menu", $"菜单重建失败，已保留上一版菜单: {ex.Message}");
            }
            UpdateLayoutIndicator();

            // UI-04:页面最大化态与常规态的窗格外观、顶栏形态在此切换。
            // WindowsChanged 是 MaximizeWindow / RestoreLayoutFromMaximized 的共同出口。
            ApplyFocusChrome();

            // R4-3:刚拖出来的浮动窗口带的是自己那份浅色令牌,补一次主题
            ApplyThemeToFloatingWindows();
            _topBar.Refresh();
        });

        // ---- 内置指令组 + 派生应用自定义指令(冲突此时报错,§5.3)
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = this,
            Docking = _docking,
            Console = _console,
            History = _history,
            Settings = settings,
            Log = log,
            Bus = _bus,
            DataDirectory = dataDirectory,
            Panels = _panels,
        });

        RegisterFrontendLifecycleCommands(registry);

        // UI-08:主题切换也是一条指令(S-02),菜单项与控制台走同一条路径
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.theme",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "切换界面主题(浅色 / 深色)",
            Example = "vulcan.app.theme mode=dark",
            RequiresUiThread = true,
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "light、dark 或 toggle",
                    Position = 0,
                    Default = "toggle",
                    AllowedValues = [ThemeLight, ThemeDark, "toggle"],
                },
            ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var mode = ctx.GetString("mode") ?? "toggle";
                if (mode.Equals("toggle", StringComparison.OrdinalIgnoreCase))
                    mode = _theme == ThemeDark ? ThemeLight : ThemeDark;
                ApplyTheme(mode, persist: true);
                return CommandResult.Ok($"界面主题已切换为 {_theme}");
            }),
        });
        // ---- 0.4.4 反哺能力:模块托管与 MCP 服务(默认关闭,ShellConfig 显式开启)
        //      注册次序在内置指令之后、派生自定义指令之前——派生应用因此可以在
        //      ConfigureCommands 里看到 module.*/mcp.* 已存在,并按需登记只读白名单。
        if (config.EnableModules || config.EnableUiModules)
        {
            _modules = config.ModuleDiscoveryRoots.Count > 0
                ? new Services.Modules.ModuleHost(
                    new Services.Modules.ZModuleDiscoverySource(config.ModuleDiscoveryRoots), log)
                : new Services.Modules.ModuleHost(
                    config.ModuleDirectory ?? Services.AppPaths.GetModulesDir(dataDirectory), log);
            _modules.EnableCommands = config.EnableModules;
            _modules.EnableUiModules = config.EnableModules || config.EnableUiModules;
            _modules.EnableFileWatching = config.ModuleDiscoveryRoots.Count == 0
                                          && (config.EnableModules || !config.EnableRemoteManagementViews);
            _modules.RequireConfirmedSources = config.RequireConfirmedModuleSources;
            _modules.UiContext = SynchronizationContext.Current;
            _modules.ShellUi = _shellUi;
            _modules.CommandWorkbench = this;

            // 候选模块的界面验收入口。只有承载界面的宿主注册它们:前端把整份注册表作为
            // FrontendCapabilityCatalog 发布到后台,后台因此拿到一份 ExecutionSite=Frontend
            // 的代理——住在无窗 --service 进程里的模块(如 Diana 的 diana.trial.load ui=true)
            // 于是能经总线中继过来建界面,而不必自己去找一个它根本没有的 IShellUiRegistrar。
            if (config.EnableUiModules)
            {
                registry.Register(new CommandDescriptor
                {
                    Name = "vulcan.module.trialui.load",
                    Domain = "vulcan",
                    CommandClass = "module",
                    Summary = "在前端临时创建候选模块界面(不进入正式模块快照)",
                    Example = "vulcan.module.trialui.load path=C:\\candidate\\z-HistoryJanus alias=janus",
                    RequiresUiThread = true,
                    Parameters =
                    [
                        new ParameterSpec
                        {
                            Name = "path",
                            Description = "候选 z 快照目录(须含 module.manifest.json 且 ui=true)",
                            Required = true,
                            Position = 0,
                        },
                        new ParameterSpec
                        {
                            Name = "alias",
                            Description = "试用界面别名,同时是 ShellUi 的注销粒度",
                            Required = true,
                            Position = 1,
                        },
                    ],
                    Handler = CommandDescriptor.Sync(context => _modules.LoadTrialUi(
                        context.RequireString("path"), context.RequireString("alias"))),
                });
                registry.Register(new CommandDescriptor
                {
                    Name = "vulcan.module.trialui.unload",
                    Domain = "vulcan",
                    CommandClass = "module",
                    Summary = "卸载前端临时候选模块界面并回收其可卸载程序集",
                    Example = "vulcan.module.trialui.unload alias=janus",
                    RequiresUiThread = true,
                    Parameters =
                    [
                        new ParameterSpec
                        {
                            Name = "alias",
                            Description = "装载时使用的试用界面别名",
                            Required = true,
                            Position = 0,
                        },
                    ],
                    Handler = CommandDescriptor.Sync(context => _modules.UnloadTrialUi(
                        context.RequireString("alias"))),
                });
            }

            registry.Register(new CommandDescriptor
            {
                Name = "vulcan.module.unload",
                Domain = "vulcan",
                CommandClass = "module",
                Summary = "卸载一个已装载模块的界面与本进程快照",
                Example = "vulcan.module.unload name=HistoryJanus",
                RequiresUiThread = true,
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
                Handler = CommandDescriptor.Sync(context =>
                    _modules.Unload(context.RequireString("name"))),
            });

            // MD-08:窗口成型前先做一次文件级面板同步,上一会话遗留的模块旁面板本次即成窗口
            if (config.ModuleDiscoveryRoots.Count == 0)
            {
                Services.Modules.ModulePanelSync.SyncFiles(
                    _modules.ModulesDirectory, Services.AppPaths.GetPanelsDir(dataDirectory), log);
            }

            // 全限定:本类的 Modules / Mcp 只读属性会遮蔽同名命名空间
            if (config.EnableModules)
                HistoryVulcan.Shell.Modules.ModuleCommands.RegisterAll(registry, _modules, settings);
        }

        if (config.EnableMcp)
        {
            var identity = config.Identity ?? Core.AppIdentity.Current;
            _prompts = new Services.Mcp.PromptGovernanceStore(dataDirectory, log);
            var audit = config.McpAuditLog
                        ?? new Services.Mcp.McpAuditRecorder(dataDirectory, log);
            Func<string, string, int, bool?> remoteConfirm = config.McpRemoteConfirm
                ?? ((_, prompt, timeout) =>
                    HistoryVulcan.Shell.Mcp.RemoteConfirmDialog.Ask(this, prompt, timeout));
            _mcp = new Services.Mcp.McpGateway(
                () => _bus, settings, log, audit, _prompts, identity, remoteConfirm);

            // McpCommands.RegisterAll 是聚合入口:内部级联注册 prompt.*(提示词治理)与
            // command.*(命令目录),不可在此重复调用 CommandCatalogCommands/PromptGovernanceCommands,
            // 否则 vulcan.command.list 等会二次注册,CommandRegistry 冲突即抛(§5.3)。
            // 全限定:本类的 Mcp 只读属性会遮蔽 HistoryVulcan.Shell.Mcp 命名空间。
            HistoryVulcan.Shell.Mcp.McpCommands.RegisterAll(registry, () => _bus, () => _mcp, settings, _prompts);

            // CX-03:MCP 中继预批准的执行直接放行,其余仍走 Shell 交互确认
            _bus.Confirmation = new Core.Mcp.GatewayAwareConfirmation(_bus.Confirmation);
        }
        else
        {
            // command.* 与中央命令集不需要网关、提示词存储或审计器。
            HistoryVulcan.Shell.Mcp.CommandCatalogCommands.RegisterCore(registry);
        }

        config.ConfigureCommands?.Invoke(registry);

        // 模块宿主在全部指令注册完成后接入并首次装载(此刻仍在 UI 线程)
        _modules?.Attach(registry, _bus, settings, dataDirectory);
        _modules?.Start();

        // 指令与模块全部就绪后再启动网关，保证首次 tools/list 即为完整注册表。
        if (_mcp != null)
        {
            var (success, message) = _mcp.TryAutostart();
            if (success)
                log.Info("mcp", message);
            else
                log.Warn("mcp", message);
        }

        // 命令结果统一进入 IShellLog/控制台；失败仍自动打开控制台查看全文。
        _bus.Executed += (text, source, result) => Dispatcher.BeginInvoke(() =>
        {
            if (!result.Success)
            {
                FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
            }
            UpdateLayoutIndicator();
        });
        log.EntryAdded += (_, entry) =>
        {
            if (entry.Level >= ShellLogLevel.Error)
            {
                Interlocked.Increment(ref _errorCount);
                Dispatcher.BeginInvoke(() =>
                {
                    UpdateErrorBadge();
                    if (entry.Level >= ShellLogLevel.Fatal)
                        FocusConsole(resetFilters: true, preserveMaximizedLayout: true);
                });
            }
        };

        // DEC-023:原 C-15 的 Ctrl + ` 本地 KeyBinding 已删除。快捷键整体归 HistoryMercury
        // （DEC-022），Shell 不再自行注册 InputBinding；需要该手势时由 Mercury 注册并指向
        // vulcan.log.focus，避免宿主与模块争夺同一组合键。

        if (config.EnableMaximizeOnDoubleClick)
        {
            DockManager.AddHandler(
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnDockDoubleClick),
                handledEventsToo: true);
        }
        BuildMenus();
        UpdateLayoutIndicator();
        ApplyFocusChrome();

        Closing += OnShellClosing;
        Closed += OnShellClosed;
    }

    /// <summary>
    /// UI-02 / UI-09.1:非客户区接管必须推迟到窗体句柄就绪 —— 派生应用常在对象
    /// 初始化器里(即构造函数返回之后)设置 WindowStyle,构造期判定会误判成标准窗体。
    /// 用事件而非 override，避免仅为内部窗体时序扩大公开面。
    /// </summary>
    private void OnShellSourceInitialized(object? sender, EventArgs e)
    {
        if (WindowStyle == WindowStyle.SingleBorderWindow)
        {
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                // 不再让隐藏标题区横跨整个窗体顶部：它会吞掉工具窗格的 ▼/×。
                // 窗体拖动只由中央文档页签行的空白区域处理。
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                // Q-4:Win10 IoT 无系统窗口圆角,窗体外缘一律直角
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            _customChrome = true;
        }
        else
        {
            // 只降级边框接管方式;顶栏的菜单与三个窗口按钮保持不变(UI-09.1)
            _customChrome = false;
            _log.Info("shell", $"宿主使用 WindowStyle={WindowStyle},已跳过非客户区接管,顶部按钮组仍然可用");
        }

        ApplyWindowStateChrome();
        ScheduleChromeReserve();
    }

    /// <summary>停靠系统门面。</summary>
    public IDockingService Docking => _docking;

    /// <summary>指令总线(派生应用 / 启动参数经此执行指令)。</summary>
    public CommandBus Commands => _bus;

    /// <summary>Adds a backend log entry to the in-memory console without writing a second log file.</summary>
    public void AddTransientLog(ShellLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // The frontend command bus already records its local echo/result. Do not show
        // the same command categories again when the backend event stream arrives.
        if (entry.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Equals(CommandBus.ResultCategory, StringComparison.OrdinalIgnoreCase))
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => AddTransientLog(entry));
            return;
        }

        _console.AddTransientEntry(entry);
        if (entry.Level < ShellLogLevel.Error)
            return;
        Interlocked.Increment(ref _errorCount);
        UpdateErrorBadge();
    }

    /// <summary>模块托管宿主(0.4.4);EnableModules=false 时为 null。</summary>
    public Services.Modules.ModuleHost? Modules => _modules;

    /// <summary>MCP 网关；仅在消费方显式启用 <see cref="ShellConfig.EnableMcp"/> 时创建。</summary>
    public Services.Mcp.McpGateway? Mcp => _mcp;

    /// <summary>提示词治理存储(0.4.4);与 <see cref="Mcp"/> 同生命周期。</summary>
    public Services.Mcp.PromptGovernanceStore? Prompts => _prompts;

    /// <summary>
    /// 命令集选中状态(0.4.4):框架的命令集窗口写入,派生应用的指令详情窗口读取。
    /// 派生应用应把自己的详情视图接到本实例,避免各建一个导致联动失效。
    /// </summary>
    public CommandSelectionState CommandSelection => _commandSelection;

    CommandBus IShellCommandWorkbenchHost.Bus => _bus;

    CommandSelectionState IShellCommandWorkbenchHost.CommandSelection => _commandSelection;

    ISettingsService IShellCommandWorkbenchHost.Settings => _settings;

    IShellLog IShellCommandWorkbenchHost.Log => _log;

    string IShellCommandWorkbenchHost.DataDirectory => _dataDirectory;

    /// <inheritdoc />
    public void AttachCommandCatalogSession(ICommandCatalogSession session)
        => _catalogSession.Attach(session);

    /// <inheritdoc />
    public void ConfigureCommandCompletionRouting(Func<bool> isConsoleFocused, Action showCommandCatalog)
        => _console.ConfigureCompletionRouting(isConsoleFocused, showCommandCatalog);

    /// <inheritdoc />
    public void RefreshCommandCompletionFocus() => _console.RefreshCompletionFocus();

    /// <summary>关于对话框文本(vulcan.app.about)。</summary>
    public string AboutText =>
        $"{_config.AppName} v{_config.AppVersion}\n\n基于 HistoryVulcan 通用窗口框架模板\n.NET 8 + WPF + AvalonDock 4.72.1";

    /// <summary>
    /// 标准窗口内容接管:描述符的停靠位置仍由派生应用声明,内容工厂换成
    /// Shell 实现;未声明时按缺省位置强制注册(控制台是架构不变量 2 的落点)。
    /// 窗口 ID 是框架与派生应用之间的对接约定：双方各自声明并按 Id 合并，
    /// 不得仅在一侧改名；新增窗口时须同步核对派生应用的 ToolWindows 表。
    /// </summary>
    private void TakeOverDescriptor(
        string id,
        string fallbackTitle,
        DockSide fallbackSide,
        double fallbackRatio,
        Func<object> factory,
        bool forcePlacement = false)
    {
        var index = _config.ToolWindows.FindIndex(
            d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _config.ToolWindows.Add(new ToolWindowDescriptor
            {
                Id = id,
                Title = fallbackTitle,
                DefaultSide = fallbackSide,
                DefaultRatio = fallbackRatio,
                ContentFactory = factory,
            });
            return;
        }

        var d0 = _config.ToolWindows[index];
        _config.ToolWindows[index] = new ToolWindowDescriptor
        {
            Id = d0.Id,
            Title = d0.Title,
            DefaultSide = forcePlacement ? fallbackSide : d0.DefaultSide,
            DefaultRatio = forcePlacement ? fallbackRatio : d0.DefaultRatio,
            DefaultTabTarget = forcePlacement ? null : d0.DefaultTabTarget,
            DefaultVisible = d0.DefaultVisible,
            IsSingleton = d0.IsSingleton,
            ContentFactory = factory,
        };
    }

    private void FocusConsole(bool resetFilters = false, bool preserveMaximizedLayout = false)
    {
        if (resetFilters)
            _console.ResetFilters();
        if (preserveMaximizedLayout && _docking.MaximizedId != null)
        {
            if (_docking.MaximizedId.Equals(StandardWindowIds.Console, StringComparison.OrdinalIgnoreCase))
                ActivateToolContent(StandardWindowIds.Console);
            return;
        }
        _docking.Show(StandardWindowIds.Console);
        ActivateToolContent(StandardWindowIds.Console);
    }

    internal void ActivateToolContent(string id)
    {
        if (_docking.FindContent(id) is IActivatableToolContent activatable)
            activatable.ActivateContent();
    }

    private async Task ShowCommandCatalogForCompletionAsync()
    {
        try
        {
            var result = await _bus.ExecuteAsync(
                $"vulcan.ui.show name={StandardWindowIds.Mcp}",
                "UI");
            if (!result.Success)
                _log.Error("console", $"切换命令集失败: {result.Message}");
        }
        catch (Exception ex)
        {
            _log.Error("console", $"切换命令集异常: {ex.GetType().Name}");
        }
    }

    private void OnDockDoubleClick(object sender, MouseButtonEventArgs e)
        => _topBar.HandleDockTabMouseLeftButtonDown(e);

    // ---------------------------------------------------------------- 顶栏状态(UI-05)

    /// <summary>UI-05.3:原状态栏错误计数,点击行为不变(聚焦控制台并只看错误)。</summary>
    private void UpdateErrorBadge()
    {
        ErrorBadge.Content = $"错误 {_errorCount}";
        ErrorBadge.Visibility = _errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnErrorBadgeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.log.focus errors=true", "UI");

    // ---------------------------------------------------------------- 顶栏窗口控件(UI-02)

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.app.window state=minimized", "UI");

    private void OnMaximizeRestoreClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.app.window state=toggle", "UI");

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => _ = _bus.ExecuteAsync("vulcan.app.hide", "UI");

    internal CommandResult SetFloatingWindowState(string id, string state)
        => _topBar.SetFloatingWindowState(id, state);

    /// <summary>窗体最大化图标切换 + WindowChrome 溢出补偿(UI-02.5)。</summary>
    private void ApplyWindowStateChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        if (TryFindResource(maximized ? "Shell.Icon.Restore" : "Shell.Icon.Maximize") is Geometry icon)
            MaximizeIcon.Data = icon;
        MaximizeButton.ToolTip = maximized ? "向下还原" : "最大化";

        // 接管非客户区后,最大化的窗体会按可调整边框宽度溢出工作区,
        // 不补偿则顶栏被裁掉一截。
        RootBorder.Padding = _customChrome && maximized
            ? SystemParameters.WindowResizeBorderThickness
            : default;
    }

    private void OnShellClosing(object? sender, CancelEventArgs e)
    {
        if (_config.CloseBehavior == ShellCloseBehavior.Hide && !_allowClose)
        {
            e.Cancel = true;
            _ = _bus.ExecuteAsync("vulcan.app.hide", "UI");
            _closing = false;
            return;
        }

        _closing = true;
        _chromeUpkeep.Stop();
        SaveWindowBounds();
        _docking.SaveCurrentLayout();

        // 0.4.4:Shell 自建的能力由 Shell 自己收尾——网关握着监听端口,
        // 模块宿主握着文件监听与防抖定时器,都必须在退出前释放。
        // 派生应用不再需要(也不应该)重复 Dispose 这两件。
        _mcp?.Dispose();
        _modules?.Dispose();
        _history.Save();
        _catalogSession.Dispose();
    }

    private void OnShellClosed(object? sender, EventArgs e)
        => _topBar.Dispose();

    private void RegisterFrontendLifecycleCommands(CommandRegistry registry)
    {
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.hide",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "隐藏 HistoryVulcan 前端窗口并保持后台连接",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Hide();
                return CommandResult.Ok("前端窗口已隐藏");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.focusconsole",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示窗口、打开并聚焦控制台，同时最大化控制台",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                FocusConsole(resetFilters: false, preserveMaximizedLayout: false);
                _docking.MaximizeWindow(StandardWindowIds.Console);
                _console.FocusInput();
                return CommandResult.Ok("控制台已显示、聚焦并最大化");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.show",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "显示并激活 HistoryVulcan 前端窗口",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
                return CommandResult.Ok("前端窗口已显示");
            }),
        }, FrontendCommandCatalog.Source);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.close",
            Domain = "vulcan",
            CommandClass = "app",
            Summary = "退出 HistoryVulcan 前端进程",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ =>
            {
                _allowClose = true;
                Close();
                return CommandResult.Ok("前端正在退出");
            }),
        }, FrontendCommandCatalog.Source);
    }

    // ---------------------------------------------------------------- 主窗体边界持久化

    private sealed record WindowBounds(double Left, double Top, double Width, double Height, bool Maximized);

    private string WindowBoundsPath => Path.Combine(_dataDirectory, "window.json");

    private void RestoreWindowBounds()
    {
        try
        {
            if (!File.Exists(WindowBoundsPath))
                return;

            var b = JsonSerializer.Deserialize<WindowBounds>(File.ReadAllText(WindowBoundsPath));
            if (b == null || b.Width < 200 || b.Height < 150)
                return;

            // 粗校验落点仍在虚拟屏幕范围内(多显示器拔除后不至于跑到屏外)
            var vLeft = SystemParameters.VirtualScreenLeft;
            var vTop = SystemParameters.VirtualScreenTop;
            var vRight = vLeft + SystemParameters.VirtualScreenWidth;
            var vBottom = vTop + SystemParameters.VirtualScreenHeight;
            if (b.Left + b.Width < vLeft + 50 || b.Left > vRight - 50 ||
                b.Top < vTop - 10 || b.Top > vBottom - 50)
            {
                return;
            }

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
            if (b.Maximized)
                WindowState = WindowState.Maximized;
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"恢复主窗体位置失败: {ex.Message}");
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var r = RestoreBounds; // 最大化时记录还原后的边界
            var b = new WindowBounds(r.Left, r.Top, r.Width, r.Height,
                WindowState == WindowState.Maximized);
            File.WriteAllText(WindowBoundsPath, JsonSerializer.Serialize(b));
        }
        catch (Exception ex)
        {
            _log.Warn("shell", $"保存主窗体位置失败: {ex.Message}");
        }
    }
}
