using System.IO;
using System.Runtime.Loader;
using System.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Modules;

/// <summary>Loads validated runtime packages and owns module commands, instances and reloads.</summary>
public sealed partial class ModuleHost : IDisposable
{
    private readonly IShellLog _log;
    private readonly object _reloadLock = new();
    private bool _disposed;
    private readonly string _dir;
    private readonly IModuleDiscoverySource _discoverySource;
    private IReadOnlyList<ModuleDiscoveryDiagnostic> _discoveryDiagnostics = [];
    private CommandRegistry? _registry;
    private CommandBus? _bus;
    private Snapshot _current = Snapshot.Empty;
    private Snapshot? _building;
    private AssemblyLoadContext[] _xamlContexts = [];
    private bool _xamlResolverInstalled;
    private readonly ModuleDirectoryWatcher _watcher;

    /// <summary>Creates a module host backed by explicit runtime package discovery.</summary>
    public ModuleHost(IModuleDiscoverySource discoverySource, IShellLog log)
    {
        ArgumentNullException.ThrowIfNull(discoverySource);
        _discoverySource = discoverySource;
        _dir = discoverySource is RuntimeModuleDiscoverySource ? discoverySource.Roots[0] : "";
        _log = log;
        _watcher = new ModuleDirectoryWatcher(log, Reload);
        EnsureXamlResolver();
    }

    /// <summary>Marshals registry mutations to the consumer thread; null executes inline.</summary>
    public SynchronizationContext? UiContext { get; set; }

    /// <summary>Whether modules marked as UI modules may be initialized.</summary>
    public bool EnableUiModules { get; set; } = true;

    /// <summary>Whether this host owns filesystem change detection for the module directory.</summary>
    public bool EnableFileWatching { get; set; } = true;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string ModulesDirectory => _dir;

    /// <summary>Configured runtime package roots.</summary>
    public IReadOnlyList<string> DiscoveryRoots => _discoverySource.Roots;

    /// <summary>Diagnostics from the most recent discovery scan.</summary>
    public IReadOnlyList<ModuleDiscoveryDiagnostic> DiscoveryDiagnostics => _discoveryDiagnostics;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<ModuleMeta> Modules => _current.Modules;

    /// <summary>当前快照持有的加载上下文数量，供服务内回滚诊断使用。</summary>
    internal int CurrentContextCount => _current.Contexts.Count;

    /// <summary>每次整体重载完成后触发(在重载线程上);MD-08 面板同步等旁路逻辑挂此处。</summary>
    public event Action? ReloadCompleted;

    /// <summary>接入指令注册表，再调用 Start；仅此重载不注入模块上下文。</summary>
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

        // Preserve the existing composition signature; module contexts expose only the bus and registration.
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Start() => Reload();

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

        // 元信息、待接入条目、接入失败与指令计数一并抹掉：清单只在 ForgetModule 里写一遍。
        // 这里此前自己列了一份，漏掉 PendingAttach 的代价不出现在卸载这一刻，
        // 而出现在下一次装同名包——AttachPhase 会把同一个模块接两遍。
        var alc = snap.ForgetModule(owner);

        if (alc != null
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
        PublishXamlContexts();
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
        => _discoverySource.Roots.Where(Directory.Exists).ToList();

    /// <summary>
    /// 把一个刚接入的模块暂存的指令登记进活登记表。
    ///
    /// 取的是快照里属于该 owner、且尚未登记的全部条目：装载阶段收集的反射指令，
    /// 与该模块 <c>Attach</c> 时经 <c>RegisterCommands</c> 暂存的指令，都在其中。
    /// </summary>
    private void RegisterModuleCommands(Snapshot snap, string owner)
    {
        if (_registry == null)
            return;

        var registered = snap.RegisteredNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var pending = snap.PendingCommands
            .Where(item =>
                item.ModuleName.Equals(owner, StringComparison.OrdinalIgnoreCase)
                && !registered.Contains(item.Descriptor.Name))
            .ToList();
        if (pending.Count == 0)
            return;

        foreach (var (descriptor, moduleName) in pending)
        {
            try
            {
                var ownedDescriptor = ModuleCommandTaxonomy.Apply(descriptor, moduleName);
                _registry.Register(ownedDescriptor, $"module:{moduleName}");
                snap.RegisteredNames.Add(descriptor.Name);
                snap.CountCommand(moduleName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // MD-07：与内置指令（或其他模块）重名，仅拒绝该方法，不静默覆盖。
                //
                // 必须连 ArgumentException 一起兜：CommandRegistry.Register 除了重名
                // （InvalidOperationException）还会因非法命令类、以及「写了 ConfirmPrompt
                // 却没升到 Ask 级」抛 ArgumentException。只兜一种的后果不是少一条指令，
                // 而是异常穿透整个 foreach——本模块排在它后面的**所有指令**一条都注册不上，
                // 且 RegisteredNames 与模块元信息停在半途。
                // 一条坏指令只该连累它自己。
                _log.Error("module", $"模块 {moduleName} 的指令 {descriptor.Name} 被拒绝注册: {ex.Message}");
            }
        }
    }

}
