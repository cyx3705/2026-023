using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Xml.Linq;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Modules;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record ModuleMeta(
    string ModuleName, string Description, string Author, string Version,
    bool Open, string AssemblyFile, int CommandCount, string Slot = "", bool Ui = false)
{
    /// <summary>当前加载快照中的唯一模块实例标识；重载后变化。</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>Absolute Z package path when the module came from manifest discovery.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute manifest path when the module came from manifest discovery.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>
    /// 上下文注入失败的原因；非空表示本模块**没有接上宿主**，它的指令一条都不会到位。
    /// </summary>
    /// <remarks>
    /// 5.0 收窄 <c>IModuleContext</c> 之后，按旧契约编译的模块会在 <c>Attach</c> 里抛
    /// <c>MissingMethodException</c>。宿主此前把它记成一条 Warn 就继续，紧接着还打一个
    /// ✓ 说装载成功——于是 <c>vulcan.module.list</c> 显示模块在位、版本正确、0 条指令，
    /// 而「为什么 0 条」只存在于日志里。
    ///
    /// 宿主不为模块补契约：模块自己去适配。但宿主必须**说实话**——
    /// 接不上就是没装上，这一列就是那句实话，列在目录里而不是埋在日志里。
    /// </remarks>
    public IReadOnlyList<string> AttachFailures { get; init; } = [];

    /// <summary>本模块是否真正接上了宿主。</summary>
    public bool Attached => AttachFailures.Count == 0;
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
    private bool _disposed;
    private string _dir;
    private IModuleDiscoverySource? _discoverySource;
    private IReadOnlyList<ModuleDiscoveryDiagnostic> _discoveryDiagnostics = [];
    private IReadOnlyList<ModuleDiscoveryEntry>? _confirmedSources;
    private CommandRegistry? _registry;
    private CommandBus? _bus;
    private Snapshot _current = Snapshot.Empty;
    private Snapshot? _building;
    private AssemblyLoadContext[] _xamlContexts = [];
    private bool _xamlResolverInstalled;
    private readonly ModuleDirectoryWatcher _watcher;

    /// <summary>按模块目录与日志建立宿主；装载与命令注册由 Attach/Start 触发。</summary>
    public ModuleHost(string modulesDir, IShellLog log)
    {
        _dir = modulesDir;
        _log = log;
        _watcher = new ModuleDirectoryWatcher(log, Reload);
        EnsureXamlResolver();
    }

    /// <summary>Creates a module host backed by explicit Z-level manifest discovery.</summary>
    public ModuleHost(IModuleDiscoverySource discoverySource, IShellLog log)
    {
        ArgumentNullException.ThrowIfNull(discoverySource);
        _discoverySource = discoverySource;
        _dir = discoverySource is RuntimeModuleDiscoverySource ? discoverySource.Roots[0] : "";
        _log = log;
        _watcher = new ModuleDirectoryWatcher(log, Reload);
        EnsureXamlResolver();
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

    /// <summary>Whether modules marked as UI modules may be initialized.</summary>
    public bool EnableUiModules { get; set; } = true;

    /// <summary>Whether this host owns filesystem change detection for the module directory.</summary>
    public bool EnableFileWatching { get; set; } = true;

    /// <summary>
    /// When true, discovery-backed hosts remain empty until a backend-confirmed manifest set is supplied.
    /// </summary>
    public bool RequireConfirmedSources { get; set; }

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

        // settings 与 dataDirectory 不再落到字段上。
        //
        // 5.0 把 Settings / DataDirectory 移出 IModuleContext 之后，这两样在 ModuleHost
        // 内部就没有任何消费方了；此前它们仍被存进字段，再用 `_ = _settings;` 两条丢弃
        // 语句压住「已赋值从未使用」的警告。那不是预留，是把死状态伪装成活的——
        // 冻结会把这个空位永久固化，而下一个读者无从判断它是待接线还是已废弃。
        //
        // 形参保留是刻意的：装配点的调用形状不因宿主内部瘦身而变动，
        // 而参数名本身说明了宿主曾经、也可能再次需要它们。
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Start()
    {
        if (_discoverySource == null)
            Directory.CreateDirectory(_dir);
        Reload();
    }

    /// <summary>module.dir path=:切换模块目录并整体重载。</summary>
    public void ChangeDirectory(string newDir)
    {
        _watcher.Stop();
        _dir = newDir;
        Directory.CreateDirectory(_dir);
        Reload();
        _log.Info("module", $"模块目录已切换: {_dir}");
    }

    /// <summary>Replaces the configured Z discovery roots and immediately reloads modules.</summary>
    public void ChangeDiscoveryRoots(IEnumerable<string> roots)
    {
        _watcher.Stop();
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
        var entries = new List<ModuleDiscoveryEntry>();
        var diagnostics = new List<ModuleDiscoveryDiagnostic>();
        foreach (var manifest in manifests)
        {
            var package = Path.GetDirectoryName(manifest)!;
            if (RuntimeModuleDiscoverySource.TryReadPackage(
                    package, out var entry, out var code, out var error)
                && entry.ManifestPath.Equals(manifest, StringComparison.OrdinalIgnoreCase))
                entries.Add(entry);
            else
                diagnostics.Add(new ModuleDiscoveryDiagnostic(manifest, code, error));
        }
        _confirmedSources = entries;
        _discoveryDiagnostics = diagnostics;
        Reload();
    }

    /// <summary>
    /// 从当前快照卸下一个已装载模块（命令、界面、可卸载程序集），不扫描磁盘、不触发整体重载。
    /// 下次 <see cref="Reload"/> 或文件变化会按运行区当前包再装回来。
    /// </summary>
    public CommandResult Unload(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("vulcan.module.unload 需要 name（vulcan.module.list 中的模块名）。");

        var key = name.Trim();
        lock (_reloadLock)
        {
            CommandResult? result = null;
            var ui = UiContext;
            if (ui != null)
                ui.Send(_ => result = UnloadFromSnapshot(key), null);
            else
                result = UnloadFromSnapshot(key);
            return result!;
        }
    }

    private CommandResult UnloadFromSnapshot(string name)
    {
        var snap = _current;
        var match = snap.Modules.FirstOrDefault(module =>
            module.ModuleName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return CommandResult.Fail(snap.Modules.Count == 0
                ? $"没有已装载的模块 {name}。"
                : $"没有已装载的模块 {name}；当前有: {string.Join("、", snap.Modules.Select(module => module.ModuleName))}");
        }

        var owner = match.ModuleName;
        if (_registry != null)
        {
            var source = $"module:{owner}";
            foreach (var command in _registry.All().ToList())
            {
                if (!_registry.GetSource(command.Name).Equals(source, StringComparison.OrdinalIgnoreCase))
                    continue;
                _registry.Unregister(command.Name);
                snap.RegisteredNames.RemoveAll(registered =>
                    registered.Equals(command.Name, StringComparison.OrdinalIgnoreCase));
            }
        }

        snap.PendingCommands.RemoveAll(item =>
            item.ModuleName.Equals(owner, StringComparison.OrdinalIgnoreCase));
        snap.Metas.RemoveAll(meta =>
            meta.Name.Equals(owner, StringComparison.OrdinalIgnoreCase));
        snap.ClearCommandCount(owner);

        if (snap.ContextsByOwner.Remove(owner, out var alc)
            && snap.ContextsByOwner.Values.All(remaining => !ReferenceEquals(remaining, alc)))
        {
            // 钉住的上下文不可回收，卸载会抛；它的实例也要留着——模块正持有进程级状态
            // （例如一条还在跑的 UI 线程），丢掉实例等于把那条线程变成孤儿。
            // 代价是钉住模块持有的端口、句柄同样留到宿主重启，这是 pinned 的固有含义。
            if (ReferenceEquals(alc, AssemblyLoadContext.Default))
            {
                _log.Info("module", $"{owner} 是钉住模块：已撤销指令，进程内状态保留至宿主重启");
            }
            else
            {
                // 必须先 Dispose 再 Drop：DropInstancesFrom 只丢引用，
                // 而托管引用被丢弃不会关闭实例持有的端口与句柄。
                // 本路径（按模块卸载）是 vulcan.module.install / remove 的必经之路，
                // 漏掉这一步的后果与整快照重载漏掉时完全一样——每装一次包，
                // 就多一个仍在监听、仍持有活总线引用的旧网关。
                DisposeInstances(snap.InstancesFrom(alc));
                snap.DropInstancesFrom(alc);
                snap.Contexts.Remove(alc);
                alc.Unload();
            }
        }

        snap.Modules.RemoveAll(module =>
            module.ModuleName.Equals(owner, StringComparison.OrdinalIgnoreCase));
        _log.Info("module", $"已卸载模块: {owner}");
        return CommandResult.Ok($"已卸载模块: {owner}");
    }

    private void SyncFileWatching()
    {
        if (!EnableFileWatching)
        {
            _watcher.Stop();
            return;
        }

        var targets = ListFileWatchTargets();
        if (!_watcher.Watch(targets))
            return;
        if (targets.Count == 1)
            _log.Info("module", $"正在监听模块目录: {targets[0]}");
        else if (targets.Count > 1)
            _log.Info("module", $"正在监听 {targets.Count} 个模块目录");
    }

    private List<string> ListFileWatchTargets()
    {
        if (_discoverySource == null)
            return string.IsNullOrWhiteSpace(_dir) ? [] : [_dir];

        if (_discoverySource is RuntimeModuleDiscoverySource)
            return _discoverySource.Roots.Where(Directory.Exists).ToList();

        var targets = new List<string>();
        foreach (var root in _discoverySource.Roots)
        {
            if (!Directory.Exists(root))
                continue;

            string[] projects;
            try
            {
                projects = Directory.GetDirectories(root, ZModuleDiscoverySource.ProjectDirectoryPattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("module", $"模块发现根不可读,跳过监听: {root}: {ex.Message}");
                continue;
            }

            foreach (var project in projects)
            {
                try
                {
                    foreach (var package in Directory.GetDirectories(project, "z-*"))
                    {
                        if (File.Exists(Path.Combine(package, ZModuleDiscoverySource.ManifestFileName)))
                            targets.Add(package);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _log.Warn("module", $"项目目录不可读,跳过监听: {project}: {ex.Message}");
                }
            }
        }

        return targets;
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // MD-07：与内置指令（或其他模块）重名，仅拒绝该方法，不静默覆盖。
                //
                // 必须连 ArgumentException 一起兜：CommandRegistry.Register 除了重名
                // （InvalidOperationException）还会因非法命令类、以及「写了 ConfirmPrompt
                // 却没升到 Ask 级」抛 ArgumentException。只兜一种的后果不是少一条指令，
                // 而是异常穿透整个 foreach——pending 里排在它后面的**所有模块**一条都注册
                // 不上，且 RegisteredNames 与 FinalizeMetas 停在半途。
                // 一条坏指令只该连累它自己。
                _log.Error("module", $"模块 {moduleName} 的指令 {descriptor.Name} 被拒绝注册: {ex.Message}");
            }
        }

        next.FinalizeMetas();
    }





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
        _building = snap;
        try
        {
            PublishXamlContexts();
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
        catch (Exception ex)
        {
            // 半成品快照必须就地拆掉。Reload 是「先拆旧、再建新」，此刻旧快照已经没了，
            // 而这个建到一半的快照没有任何人持有引用——它建好的可回收 ALC 因此永远
            // 等不到 Unload，继续锁着模块 DLL。症状出现在很远的地方：下一次
            // vulcan.module.install 报文件被占用，而唯一的恢复手段是重启宿主。
            //
            // 拆完照样抛：调用方要知道这轮装载失败了，不能拿一个空快照假装成功。
            _log.Error("module", $"装载模块快照失败，已回收本轮建立的上下文: {ex.Message}");
            TeardownSnapshot(snap);
            throw;
        }
        finally
        {
            _building = null;
            PublishXamlContexts();
        }
    }

    /// <summary>
    /// 钉住模块的包目录。
    ///
    /// <c>pinned: true</c> 仍装进 <see cref="AssemblyLoadContext.Default"/>，不可卸载。
    /// 可热重载的 WPF 模块走另一条路：装进可回收 ALC，默认上下文的 Resolving 只
    /// 返回该 ALC 里已经装好的程序集，绝不 <c>LoadFromAssemblyPath</c> 进 Default。
    /// 后一条才能让 XAML 的 <c>assembly=AvalonDock.Themes.VS2013</c> 解析成功，同时允许 Unload。
    /// </summary>
    private string[] _pinnedPackages = [];

    private bool _pinnedResolverInstalled;

    private void LoadDiscoveredModule(Snapshot snap, ModuleDiscoveryEntry module)
    {
        if (module.Ui && !EnableUiModules)
        {
            _log.Info("module.discovery", $"离线组合跳过 UI 模块 {module.Name}: 当前执行目标不提供桌面运行时");
            return;
        }

        AssemblyLoadContext alc;
        Assembly assembly;
        try
        {
            if (ReadPinnedFlag(module.ManifestPath))
            {
                alc = AssemblyLoadContext.Default;
                assembly = LoadPinned(module);
            }
            else
            {
                var owned = new ModuleLoadContext(module.PackagePath);
                TrackContext(snap, owned);
                alc = owned;
                // XAML 的 assembly= 简单名走默认上下文。必须先把包内依赖装进
                // 这个可回收 ALC，Resolving 才能交回已装载的程序集。
                foreach (var dependency in module.DependencyPaths)
                    LoadAssembly(owned, dependency);
                assembly = LoadAssembly(owned, module.ArtifactPath);
            }

            ScanAssembly(
                snap,
                assembly,
                module.ArtifactPath,
                module.PackagePath,
                module.Ui,
                module,
                alc);
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
        => RuntimeModuleDiscoverySource.IsTransientPackageDirectory(name);

    private void LoadGroup(Snapshot snap, string dir, string slot, bool uiEnabled)
    {
        if (uiEnabled && !EnableUiModules)
        {
            _log.Info("module.discovery", $"离线组合跳过 UI 模块目录: {dir}");
            return;
        }

        var dlls = Directory.GetFiles(dir, "*.dll");
        if (dlls.Length == 0)
            return;

        var alc = new ModuleLoadContext(dir);
        TrackContext(snap, alc);

        foreach (var dll in dlls)
        {
            try
            {
                var asm = LoadAssembly(alc, dll);
                ScanAssembly(snap, asm, dll, slot, uiEnabled, null, alc);
            }
            catch (Exception ex)
            {
                // MD-06:坏 DLL 只自身下线并告警,不影响宿主与其他模块
                _log.Warn("module", $"跳过 {(slot.Length > 0 ? slot + "/" : "")}{Path.GetFileName(dll)}: {ex.Message}");
            }
        }
    }

    private void ScanAssembly(
        Snapshot snap,
        Assembly asm,
        string dllPath,
        string slot,
        bool uiEnabled,
        ModuleDiscoveryEntry? discovered,
        AssemblyLoadContext alc)
    {
        var fileName = Path.GetFileName(dllPath);
        var types = LoadTypes(asm);

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
            {
                _log.Warn("module",
                    $"跳过 {fileName}：程序集声明 {declaredName} {declaredVersion}，"
                    + $"与 manifest {discovered.Name} {discovered.Version} 不一致");
                continue;
            }
            var moduleName = discovered?.Name ?? declaredName;
            var commandPrefix = GetProp(info, "CommandPrefix") as string ?? moduleName;
            if (!contextAttached)
            {
                AttachModuleContexts(snap, types, moduleName);
                contextAttached = true;
            }

            var attachFailures = snap.AttachFailures.GetValueOrDefault(moduleName);

            snap.Metas.Add((moduleName,
                GetProp(info, "Description") as string ?? "",
                GetProp(info, "Author") as string ?? "",
                discovered?.Version ?? declaredVersion,
                open, fileName, slot, uiEnabled,
                discovered?.PackagePath, discovered?.ManifestPath));
            snap.ContextsByOwner[moduleName] = alc;

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

            var origin = $"← {(slot.Length > 0 ? slot + "/" : "")}{fileName}";
            if (attachFailures is { Count: > 0 })
            {
                // 接不上宿主就是没装上。打 ✓ 会让 vulcan.module.list 显示模块在位、
                // 版本正确、0 条指令，而原因只在日志里——那正是本次排查绕的弯路。
                _log.Error("module",
                    $"✗ 模块 {moduleName} {GetProp(info, "Version")} 未接上宿主，指令不会注册 {origin}"
                    + Environment.NewLine + "    " + string.Join(Environment.NewLine + "    ", attachFailures));
            }
            else
            {
                _log.Info("module",
                    $"✓ 模块 {moduleName} {GetProp(info, "Version")} ({(open ? "全暴露" : "精准暴露")}) {origin}");
            }
        }
    }

    /// <summary>
    /// 读 manifest 的 <c>pinned</c> 标志：声明本模块**不可热重载**。
    ///
    /// 不走 <see cref="ModuleDiscoveryEntry"/>：那是已冻结的公开记录，为一个标志改它的
    /// 构造函数是破坏性变更。与 <see cref="ReadUiFlag"/> 同一模式——宿主自己读文件。
    /// </summary>
    private static bool ReadPinnedFlag(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return doc.RootElement.TryGetProperty("pinned", out var value)
                   && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception)
        {
            return false;
        }
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
}
