using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Modules;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record ModuleMeta(
    string ModuleName, string Description, string Author, string Version,
    bool Open, string AssemblyFile, int CommandCount, string Slot = "", bool Ui = false)
{
    /// <summary>Absolute Z package path when the module came from manifest discovery.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute manifest path when the module came from manifest discovery.</summary>
    public string? ManifestPath { get; init; }
}

/// <summary>
/// 模块宿主(MD-01~07):进程内移植自 b-Code-MyAPI-Lite 的 ModuleHost/Invoker 机制(D3)。
/// 监听 Modules 目录,把含 BaseVariable.ModuleInfoBase 子类(鸭子类型,MD-02)的 DLL
/// 的业务方法注册为总线指令「模块名.方法名」;XML 注释成为帮助文本(MD-03)。
/// DLL 从内存流加载不锁文件,覆盖/新增/删除触发整体热重载(800ms 防抖,MD-01);
/// 与内置指令重名的方法拒绝注册并告警(MD-07);模块异常由总线兜底(MD-06)。
/// 与 Lite 的差异:端点表 → CommandRegistry 注册/注销;Console → IShellLog;
/// 注册表变更经 WPF Dispatcher 序列化到 UI 线程。
/// </summary>
public sealed partial class ModuleHost : IDisposable
{
    private readonly IShellLog _log;
    private readonly object _reloadLock = new();
    private string _dir;
    private IModuleDiscoverySource? _discoverySource;
    private IReadOnlyList<ModuleDiscoveryDiagnostic> _discoveryDiagnostics = [];
    private IReadOnlyList<ModuleDiscoveryEntry>? _confirmedSources;
    private CommandRegistry? _registry;
    private CommandBus? _bus;
    private ISettingsService? _settings;
    private string? _dataDirectory;
    private Snapshot _current = Snapshot.Empty;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    private Func<string, string?>? _moduleOfCommandResolver;
    private Func<string, string?>? _moduleExposureResolver;
    private Func<string, string?>? _previousModuleOfCommandResolver;
    private Func<string, string?>? _previousModuleExposureResolver;
    private bool _mcpPolicyBound;

    /// <summary>按模块目录与日志建立宿主；装载与命令注册由 Attach/Start 触发。</summary>
    public ModuleHost(string modulesDir, IShellLog log)
    {
        _dir = modulesDir;
        _log = log;
    }

    /// <summary>Creates a module host backed by explicit Z-level manifest discovery.</summary>
    public ModuleHost(IModuleDiscoverySource discoverySource, IShellLog log)
    {
        ArgumentNullException.ThrowIfNull(discoverySource);
        _discoverySource = discoverySource;
        _dir = "";
        _log = log;
    }

    /// <summary>
    /// UI 线程编组通道(0.4.4)。注册表是 UI 线程消费的普通字典,热重载换血必须编组过去。
    /// 原实现直接取 <c>System.Windows.Application.Current.Dispatcher</c>,使本类带上 WPF 依赖、
    /// 无法留在 net8.0 的 Services 层(违反 §14.2「只有 Shell 认识 WPF」)。
    /// 现与 <c>CommandBus.UiContext</c> 同一惯例,由装配点在 UI 线程赋值;
    /// 为 null 视为应用退出中,与原来 Dispatcher 为 null 的处置一致。
    /// </summary>
    public SynchronizationContext? UiContext { get; set; }

    /// <summary>是否把模块方法注册到本进程指令表。无窗前端可关闭。</summary>
    public bool EnableCommands { get; set; } = true;

    /// <summary>是否实例化模块 UI。无窗服务进程必须关闭。</summary>
    public bool EnableUiModules { get; set; } = true;

    /// <summary>Whether this host owns filesystem change detection for the module directory.</summary>
    public bool EnableFileWatching { get; set; } = true;

    /// <summary>
    /// When true, discovery-backed hosts remain empty until a backend-confirmed manifest set is supplied.
    /// </summary>
    public bool RequireConfirmedSources { get; set; }

    /// <summary>模块内嵌界面的宿主注册器;无窗服务进程保持 null。</summary>
    public IShellUiRegistrar? ShellUi { get; set; }

    /// <summary>命令工作台挂载点；无窗服务进程或未装配 Shell 时保持 null。</summary>
    public IShellCommandWorkbenchHost? CommandWorkbench { get; set; }


    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string ModulesDirectory => _dir;

    /// <summary>Configured Z-level discovery roots; empty for the legacy directory host.</summary>
    public IReadOnlyList<string> DiscoveryRoots => _discoverySource?.Roots ?? [];

    /// <summary>Diagnostics from the most recent discovery scan.</summary>
    public IReadOnlyList<ModuleDiscoveryDiagnostic> DiscoveryDiagnostics => _discoveryDiagnostics;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<ModuleMeta> Modules => _current.Modules;

    /// <summary>每次整体重载完成后触发(在重载线程上);MD-08 面板同步等旁路逻辑挂此处。</summary>
    public event Action? ReloadCompleted;

    /// <summary>接入指令注册表(ShellWindow 创建后调用,再 Start)。</summary>
    public void Attach(CommandRegistry registry)
    {
        _registry = registry;
        BindMcpExposurePolicy();
    }

    /// <summary>接入模块业务运行所需的完整宿主上下文。</summary>
    public void Attach(
        CommandRegistry registry,
        CommandBus bus,
        ISettingsService settings,
        string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(bus);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        if (!ReferenceEquals(bus.Registry, registry))
            throw new ArgumentException("模块宿主的 CommandBus 必须使用同一个 CommandRegistry。", nameof(bus));

        _registry = registry;
        _bus = bus;
        _settings = settings;
        _dataDirectory = Path.GetFullPath(dataDirectory);
        BindMcpExposurePolicy();
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Start()
    {
        if (_discoverySource == null)
            Directory.CreateDirectory(_dir);
        Reload();
        if (_discoverySource == null && EnableFileWatching)
        {
            StartWatcher();
            _log.Info("module", $"正在监听模块目录: {_dir}");
        }
    }

    /// <summary>module.dir path=:切换模块目录并整体重载。</summary>
    public void ChangeDirectory(string newDir)
    {
        _watcher?.Dispose();
        _watcher = null;
        _dir = newDir;
        Directory.CreateDirectory(_dir);
        Reload();
        if (EnableFileWatching)
            StartWatcher();
        _log.Info("module", $"模块目录已切换: {_dir}");
    }

    /// <summary>Replaces the configured Z discovery roots and immediately reloads modules.</summary>
    public void ChangeDiscoveryRoots(IEnumerable<string> roots)
    {
        _watcher?.Dispose();
        _watcher = null;
        _confirmedSources = null;
        _discoverySource = new ZModuleDiscoverySource(roots);
        Reload();
        _log.Info("module", $"模块发现根已切换: {string.Join(";", _discoverySource.Roots)}");
    }

    /// <summary>
    /// Reloads UI modules from a backend-confirmed manifest set without performing an independent scan.
    /// </summary>
    public void ReloadConfirmedSources(IEnumerable<string> manifestPaths)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        var manifests = manifestPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToList();
        if (manifests.Count == 0)
        {
            _confirmedSources = [];
            _discoveryDiagnostics = [];
            Reload();
            return;
        }
        var roots = manifests
            .Select(path => Directory.GetParent(
                Directory.GetParent(Directory.GetParent(path)!.FullName)!.FullName)!.FullName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var snapshot = new ZModuleDiscoverySource(roots).Discover();
        var requested = manifests.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _confirmedSources = snapshot.Modules
            .Where(module => requested.Contains(module.ManifestPath))
            .ToList();
        _discoveryDiagnostics = snapshot.Diagnostics;
        Reload();
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_dir)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                           | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = true, // V2.2 MH-03:模块槽子目录同样触发热重载
        };
        _watcher.Created += (_, e) => OnFileEvent(e.Name);
        _watcher.Changed += (_, e) => OnFileEvent(e.Name);
        _watcher.Deleted += (_, e) => OnFileEvent(e.Name);
        _watcher.Renamed += (_, e) => OnFileEvent(e.Name);
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(string? file)
    {
        // 只关心模块本体、XML 注释文档与模块旁面板(MD-08)
        var ext = Path.GetExtension(file ?? "").ToLowerInvariant();
        if (ext is ".dll" or ".xml"
            || (file?.EndsWith(".panel.json", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            ScheduleReload(file);
        }
    }

    /// <summary>文件事件防抖:拷贝大 DLL 会触发多次 Changed,静默 800ms 后才真正重载。</summary>
    private void ScheduleReload(string? file)
    {
        _log.Info("module", $"检测到模块变化: {file ?? "?"},准备热重载...");
        lock (_reloadLock)
        {
            _debounce ??= new Timer(_ =>
            {
                try
                {
                    Reload();
                }
                catch (Exception ex)
                {
                    _log.Error("module", $"热重载失败: {ex.Message}");
                }
            }, null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(800, Timeout.Infinite);
        }
    }

    /// <summary>整体重载:构建新快照 → 注册表换血(UI 线程) → 卸载旧 ALC。</summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            var next = Build();

            // 注册表是 UI 线程消费的普通字典,变更必须编组到 UI 线程序列化。
            // Send 是同步编组,与原 Dispatcher.Invoke 等价。
            var ui = UiContext;
            if (ui == null)
            {
                // ServiceHost has no WPF synchronization context. It still owns
                // the command snapshot, so commit it directly on the service thread.
                SwapRegistrations(_current, next);
                var old = _current;
                _current = next;
                foreach (var alc in old.Contexts)
                    alc.Unload();
                _log.Info("module",
                    $"模块装载完成(无 UI): {next.Modules.Count} 个模块/{next.RegisteredNames.Count} 条指令");
            }
            else
            {
                ui.Send(_ =>
                {
                    DestroyUi(_current);
                    SwapRegistrations(_current, next);
                    CreateUi(next);
                }, null);

                var old = _current;
                _current = next;
                foreach (var alc in old.Contexts)
                    alc.Unload();

                _log.Info("module",
                    $"模块装载完成: {next.Modules.Count} 个模块,{next.RegisteredNames.Count} 条指令");
            }
        }

        ReloadCompleted?.Invoke();
    }

    private void SwapRegistrations(Snapshot old, Snapshot next)
    {

        if (_registry == null)
        {
            next.FinalizeMetas();
            return;
        }

        // UI-only 前端(EnableCommands=false)不装反射业务指令，但仍须把
        // RegisterCommands 暂存的 RequiresUiThread 页面状态命令写入本机总线；
        // 否则 HistoryMinerva.convert 一类点击会被 RemoteExecutor 转到服务侧静默失败。
        var pending = EnableCommands
            ? next.PendingCommands
            : next.PendingCommands
                .Where(item => item.Descriptor.RequiresUiThread)
                .ToList();

        if (pending.Count == 0 && !EnableCommands)
        {
            foreach (var name in old.RegisteredNames)
                _registry.Unregister(name);
            next.FinalizeMetas();
            return;
        }

        // 模块只能占用自己的一级域。先移除旧模块和同名前端代理，再用真实命令
        // 元数据推导宿主保留域，避免维护一份会随功能漂移的名称名单。
        var pendingNames = pending
            .Select(item => item.Descriptor.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in old.RegisteredNames)
            _registry.Unregister(name);

        // ShellServiceClient 会把前端模块命令投影成 frontend:* 代理。代理不是宿主保留命令，
        // 重载时必须让新模块实现接管同名命令，否则 vulcan.module.list 与 vulcan.command.list 会数量不一致。
        foreach (var command in _registry.All()
                     .Where(command =>
                         _registry.GetSource(command.Name)
                             .StartsWith("frontend:", StringComparison.OrdinalIgnoreCase)
                         && pendingNames.Contains(command.Name)))
        {
            _registry.Unregister(command.Name);
        }

        foreach (var (descriptor, moduleName) in pending)
        {
            try
            {
                var ownedDescriptor = ModuleCommandTaxonomy.Apply(descriptor, moduleName);
                _registry.Register(ownedDescriptor, $"module:{moduleName}");
                next.RegisteredNames.Add(descriptor.Name);
                next.CountCommand(moduleName);
            }
            catch (InvalidOperationException ex)
            {
                // MD-07:与内置指令(或其他模块)重名,仅拒绝该方法,不静默覆盖
                _log.Warn("module", $"模块 {moduleName} 的指令被拒绝注册: {ex.Message}");
            }
        }

        next.FinalizeMetas();
    }

    private void BindMcpExposurePolicy()
    {
        if (_moduleOfCommandResolver == null)
            _moduleOfCommandResolver = ResolveModuleOfCommand;
        if (_moduleExposureResolver == null)
            _moduleExposureResolver = ResolveModuleExposure;
        if (!_mcpPolicyBound)
        {
            _previousModuleOfCommandResolver = McpExposurePolicy.ModuleOfCommand;
            _previousModuleExposureResolver = McpExposurePolicy.ModuleExposure;
            _mcpPolicyBound = true;
        }

        // The process-level policy follows the single ModuleHost that owns the
        // authoritative registry. Both resolvers read live state so a reload
        // changes exposure without rebuilding the MCP gateway.
        McpExposurePolicy.ModuleOfCommand = _moduleOfCommandResolver;
        McpExposurePolicy.ModuleExposure = _moduleExposureResolver;
    }

    private string? ResolveModuleOfCommand(string commandName)
    {
        if (_registry == null || !_registry.TryGet(commandName, out _))
            return null;

        var source = _registry.GetSource(commandName);
        return source.StartsWith("module:", StringComparison.OrdinalIgnoreCase)
            ? source["module:".Length..]
            : null;
    }

    private string? ResolveModuleExposure(string moduleName)
        => _current.McpExposures.GetValueOrDefault(moduleName);


    /// <summary>
    /// 返回按旧命令名前缀计算的冲突集合。仅保留给旧消费方诊断；
    /// 3.2.1 起模块实际域由 module owner 决定，装载路径不再调用本方法。
    /// </summary>
    public static IReadOnlySet<string> FindModuleDomainConflicts(
        IEnumerable<string> reservedCommandNames,
        IEnumerable<string> moduleCommandNames)
    {
        ArgumentNullException.ThrowIfNull(reservedCommandNames);
        ArgumentNullException.ThrowIfNull(moduleCommandNames);

        var reserved = CommandRegistry.DomainsOf(reservedCommandNames);
        var moduleDomains = new HashSet<string>(
            CommandRegistry.DomainsOf(moduleCommandNames),
            StringComparer.OrdinalIgnoreCase);
        moduleDomains.IntersectWith(reserved);
        return moduleDomains;
    }

    // ---------------------------------------------------------------- 快照构建

    private Snapshot Build()
    {
        var snap = new Snapshot();
        if (_discoverySource != null)
        {
            if (RequireConfirmedSources && _confirmedSources == null)
                return snap;
            var discovery = _confirmedSources == null
                ? _discoverySource.Discover()
                : new ModuleDiscoverySnapshot(
                    _discoverySource.Roots,
                    _confirmedSources,
                    _discoveryDiagnostics);
            _discoveryDiagnostics = discovery.Diagnostics;
            foreach (var diagnostic in discovery.Diagnostics)
                _log.Warn("module.discovery", $"[{diagnostic.Code}] {diagnostic.Path}: {diagnostic.Message}");
            foreach (var module in discovery.Modules)
                LoadDiscoveredModule(snap, module);
            return snap;
        }

        if (!Directory.Exists(_dir))
            return snap;

        // 根目录平铺 DLL(V2-M3 既有行为):共享一个 ALC
        LoadGroup(snap, _dir, slot: "", ReadUiFlag(_dir));

        // 模块槽(V2.2 MH-01):每个一级子目录一个独立可回收 ALC,
        // 槽内依赖只在槽内解析(MH-02),槽间同名依赖不同版互不冲突
        foreach (var slotDir in Directory.GetDirectories(_dir))
        {
            var slot = Path.GetFileName(slotDir);
            if (IsModuleArtifactDirectory(slot))
            {
                _log.Log(ShellLogLevel.Debug, "module", $"忽略模块目录产物: {slot}");
                continue;
            }

            LoadGroup(snap, slotDir, slot, ReadUiFlag(slotDir));
        }

        return snap;
    }

    private void LoadDiscoveredModule(Snapshot snap, ModuleDiscoveryEntry module)
    {
        var alc = new ModuleLoadContext(module.PackagePath);
        snap.Contexts.Add(alc);
        try
        {
            var assembly = LoadAssembly(alc, module.ArtifactPath);
            ScanAssembly(
                snap,
                assembly,
                module.ArtifactPath,
                module.PackagePath,
                module.Ui,
                module);
        }
        catch (Exception ex)
        {
            _discoveryDiagnostics =
            [
                .. _discoveryDiagnostics,
                new ModuleDiscoveryDiagnostic(module.ManifestPath, "load-failed", ex.Message),
            ];
            _log.Warn("module.discovery", $"跳过 {module.Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 模块目录同时承载热重载和发布工具的暂存内容。回滚/备份目录仍然包含
    /// DLL 和 manifest，但不是活动模块，不能被扫描成第二个同名模块。
    /// </summary>
    private static bool IsModuleArtifactDirectory(string name)
        => name.StartsWith(".", StringComparison.Ordinal)
           || name.Contains("-rollback-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-backup-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-staging-", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-rollback", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-backup", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-staging", StringComparison.OrdinalIgnoreCase);

    private void LoadGroup(Snapshot snap, string dir, string slot, bool uiEnabled)
    {
        var dlls = Directory.GetFiles(dir, "*.dll");
        if (dlls.Length == 0)
            return;

        var alc = new ModuleLoadContext(dir);
        snap.Contexts.Add(alc);

        foreach (var dll in dlls)
        {
            try
            {
                var asm = LoadAssembly(alc, dll);
                ScanAssembly(snap, asm, dll, slot, uiEnabled, null);
            }
            catch (Exception ex)
            {
                // MD-06:坏 DLL 只自身下线并告警,不影响宿主与其他模块
                _log.Warn("module", $"跳过 {(slot.Length > 0 ? slot + "/" : "")}{Path.GetFileName(dll)}: {ex.Message}");
            }
        }
    }

    private static Assembly LoadAssembly(ModuleLoadContext alc, string dll)
    {
        // 按程序集名加载可命中 ALC 缓存,避免同名程序集(被其他模块作为依赖引用过)二次加载
        try
        {
            return alc.LoadFromAssemblyName(new AssemblyName(Path.GetFileNameWithoutExtension(dll)));
        }
        catch
        {
            return alc.LoadFromStream(new MemoryStream(ReadFileWithRetry(dll)));
        }
    }

    private void ScanAssembly(
        Snapshot snap,
        Assembly asm,
        string dllPath,
        string slot,
        bool uiEnabled,
        ModuleDiscoveryEntry? discovered)
    {
        var fileName = Path.GetFileName(dllPath);

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t != null).ToArray()!;
        }

        // 只托管含 ModuleInfoBase 子类的程序集;其余 DLL 视为纯依赖库(MD-02)
        var infoTypes = types.Where(t => t.IsPublic && !t.IsAbstract && IsModuleInfo(t)).ToList();
        if (infoTypes.Count == 0)
            return;

        if (discovered != null)
        {
            var identityMatches = infoTypes.Any(infoType =>
            {
                try
                {
                    var info = Activator.CreateInstance(infoType)!;
                    return string.Equals(
                               GetProp(info, "ModuleName") as string,
                               discovered.Name,
                               StringComparison.OrdinalIgnoreCase)
                           && string.Equals(
                               GetProp(info, "Version") as string,
                               discovered.Version,
                               StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
            if (!identityMatches)
            {
                var message = $"manifest 身份 {discovered.Name} {discovered.Version} 与程序集声明不一致。";
                _discoveryDiagnostics =
                [
                    .. _discoveryDiagnostics,
                    new ModuleDiscoveryDiagnostic(discovered.ManifestPath, "identity-mismatch", message),
                ];
                _log.Warn("module.discovery", message);
                return;
            }
        }

        var discoveredOwner = discovered?.Name;

        if (uiEnabled && EnableUiModules)
        {
            var owner = discoveredOwner ?? (slot.Length > 0 ? slot : Path.GetFileNameWithoutExtension(dllPath));
            foreach (var uiType in types.Where(type =>
                         type.IsPublic && !type.IsAbstract && typeof(IUiModule).IsAssignableFrom(type)))
            {
                try
                {
                    var instance = (IUiModule)snap.GetInstance(uiType);
                    if (instance is IShellUiAware aware && ShellUi != null)
                        aware.ShellUi = ShellUi;
                    if (instance is IShellCommandWorkbenchAware workbenchAware)
                        workbenchAware.CommandWorkbench = CommandWorkbench;
                    snap.UiModules.Add((instance, owner));
                }
                catch (Exception ex)
                {
                    _log.Warn("module", $"实例化 UI 模块 {uiType.FullName} 失败: {ex.Message}");
                }
            }
        }

        var docs = XmlDocs.TryLoad(dllPath, _log);
        var contextAttached = false;

        foreach (var infoType in infoTypes)
        {
            object info;
            try
            {
                info = Activator.CreateInstance(infoType)!;
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"实例化 {infoType.FullName} 失败: {ex.Message}");
                continue;
            }

            if (GetProp(info, "Enabled") is false)
                continue;

            var open = GetProp(info, "Open") is true;
            var declaredName = GetProp(info, "ModuleName") as string ?? asm.GetName().Name ?? fileName;
            var declaredVersion = GetProp(info, "Version") as string ?? "";
            if (discovered != null
                && (!declaredName.Equals(discovered.Name, StringComparison.OrdinalIgnoreCase)
                    || !declaredVersion.Equals(discovered.Version, StringComparison.OrdinalIgnoreCase)))
                continue;
            var moduleName = discovered?.Name ?? declaredName;
            var commandPrefix = GetProp(info, "CommandPrefix") as string ?? moduleName;
            if (discovered != null)
                snap.McpExposures[moduleName] = discovered.McpExposure;
            if (!contextAttached)
            {
                AttachModuleContexts(snap, types, moduleName);
                contextAttached = true;
            }

            snap.Metas.Add((moduleName,
                GetProp(info, "Description") as string ?? "",
                GetProp(info, "Author") as string ?? "",
                discovered?.Version ?? declaredVersion,
                open, fileName, slot, uiEnabled,
                discovered?.PackagePath, discovered?.ManifestPath));

            if (!EnableCommands)
            {
                // UI-only 前端仍保留模块元信息，但不重复注册服务端业务指令。
            }
            else if (open)
            {
                foreach (var t in types.Where(t =>
                             t.IsClass && t.IsPublic && !t.IsAbstract
                             && !t.IsGenericTypeDefinition && !IsModuleInfo(t)))
                    CollectType(snap, moduleName, commandPrefix, t, docs);
            }
            else if (GetProp(info, "MainClassType") is Type main)
            {
                CollectType(snap, moduleName, commandPrefix, main, docs);
            }

            _log.Info("module",
                $"✓ 模块 {moduleName} {GetProp(info, "Version")} ({(open ? "全暴露" : "精准暴露")}) " +
                $"← {(slot.Length > 0 ? slot + "/" : "")}{fileName}");
        }
    }

    private void AttachModuleContexts(
        Snapshot snap,
        IReadOnlyList<Type> types,
        string owner)
    {
        var contextTypes = types.Where(type =>
            type.IsPublic && !type.IsAbstract
            && typeof(IModuleContextAware).IsAssignableFrom(type));

        foreach (var contextType in contextTypes)
        {
            if (_bus == null || _settings == null || string.IsNullOrWhiteSpace(_dataDirectory))
            {
                _log.Warn("module",
                    $"模块 {owner} 请求宿主业务上下文，但当前装配点只提供了命令注册表；已跳过 {contextType.FullName}");
                continue;
            }

            try
            {
                var module = (IModuleContextAware)snap.GetInstance(contextType);
                module.Attach(new ModuleContext(
                    snap,
                    owner,
                    _bus,
                    _settings,
                    _log,
                    _dataDirectory));
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"注入模块上下文失败 ({contextType.FullName}): {ex.Message}");
            }
        }
    }

    /// <summary>把一个业务类的公共方法收集为待注册指令(MD-03/04)。</summary>
    private void CollectType(
        Snapshot snap,
        string moduleName,
        string commandPrefix,
        Type type,
        XmlDocs? docs)
    {
        var ns = type.Namespace ?? "Global";
        foreach (var m in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.IsSpecialName || m.IsGenericMethodDefinition || IsModuleLifecycleMethod(type, m))
                continue;

            var commandName = $"{commandPrefix}.{m.Name}";
            if (snap.PendingCommands.Any(p =>
                    p.Descriptor.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Warn("module", $"模块 {moduleName} 内方法重名,跳过 {type.Name}.{m.Name}");
                continue;
            }

            var (summary, paramDocs) = docs?.ForMethod(ns, type.Name, m.Name) ?? ("", EmptyDocs);
            snap.PendingCommands.Add((BuildDescriptor(snap, commandName, moduleName, type, m, summary, paramDocs), moduleName));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDocs = new Dictionary<string, string>();

    private static bool IsModuleLifecycleMethod(Type type, MethodInfo method)
    {
        Type[] lifecycleContracts =
        [
            typeof(IModuleContextAware),
            typeof(IUiModule),
        ];

        foreach (var contract in lifecycleContracts)
        {
            if (!contract.IsAssignableFrom(type))
                continue;

            var map = type.GetInterfaceMap(contract);
            if (map.TargetMethods.Contains(method))
                return true;
        }

        return false;
    }

    private static bool ReadUiFlag(string directory)
    {
        var path = Path.Combine(directory, "module.manifest.json");
        if (!File.Exists(path))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("ui", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void CreateUi(Snapshot snapshot)
    {
        foreach (var (module, _) in snapshot.UiModules)
        {
            try
            {
                module.CreateUi();
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"创建 UI 模块 {module.GetType().FullName} 失败: {ex.Message}");
            }
        }
    }

    private void DestroyUi(Snapshot snapshot)
    {
        foreach (var (module, owner) in snapshot.UiModules)
        {
            try
            {
                module.DestroyUi();
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"销毁 UI 模块 {module.GetType().FullName} 失败: {ex.Message}");
            }

            try
            {
                ShellUi?.UnregisterOwner(owner);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"回收模块界面失败 ({owner}): {ex.Message}");
            }
        }
    }
}
