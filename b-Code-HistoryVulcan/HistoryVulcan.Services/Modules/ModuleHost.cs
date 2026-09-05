using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Modules;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record ModuleMeta(
    string ModuleName, string Description, string Author, string Version,
    bool Open, string AssemblyFile, int CommandCount, string Slot = "", bool Ui = false)
{
    /// <summary>当前加载快照中的唯一模块实例标识；重载后变化。</summary>
    public string InstanceId { get; init; } = "";
    /// <summary>Absolute runtime package path.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Absolute runtime manifest path.</summary>
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
    /// 拆掉旧快照的登记，给新快照让位。
    ///
    /// 5.1.3 之前这里同时做「拆旧」和「装新」，而装新是一次性的：全部模块接完之后
    /// 才有第一条模块指令进活登记表。现在装新按模块拆开，交给
    /// <c>AttachPhase</c> → <see cref="RegisterModuleCommands"/> 逐个进行；
    /// 拆旧仍必须整体先做——同名指令不能新旧并存。
    /// </summary>
    private void ClearOldRegistrations(Snapshot old)
    {
        if (_registry == null)
            return;

        foreach (var name in old.RegisteredNames)
            _registry.Unregister(name);
    }

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

    // ---------------------------------------------------------------- 快照构建

    private Snapshot Build()
    {
        var snap = new Snapshot();
        _building = snap;
        try
        {
            PublishXamlContexts();
            var discovery = _discoverySource.Discover();
            _discoveryDiagnostics = discovery.Diagnostics;
            foreach (var diagnostic in discovery.Diagnostics)
                _log.Warn("module.discovery", $"[{diagnostic.Code}] {diagnostic.Path}: {diagnostic.Message}");
            foreach (var module in OrderForStartup(discovery.Modules))
                LoadDiscoveredModule(snap, module);
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

    private void ScanAssembly(
        Snapshot snap,
        Assembly asm,
        string dllPath,
        string slot,
        bool uiEnabled,
        ModuleDiscoveryEntry discovered,
        AssemblyLoadContext alc)
    {
        var fileName = Path.GetFileName(dllPath);
        var types = LoadTypes(asm);

        // 只托管含 ModuleInfoBase 子类的程序集;其余 DLL 视为纯依赖库(MD-02)
        var infoTypes = types.Where(t => t.IsPublic && !t.IsAbstract && IsModuleInfo(t)).ToList();
        if (infoTypes.Count == 0)
            return;

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
            if (!declaredName.Equals(discovered.Name, StringComparison.OrdinalIgnoreCase)
                    || !declaredVersion.Equals(discovered.Version, StringComparison.OrdinalIgnoreCase))
            {
                _log.Warn("module",
                    $"跳过 {fileName}：程序集声明 {declaredName} {declaredVersion}，"
                    + $"与 manifest {discovered.Name} {discovered.Version} 不一致");
                continue;
            }
            var moduleName = discovered.Name;
            var commandPrefix = GetProp(info, "CommandPrefix") as string ?? moduleName;

            snap.Metas.Add((moduleName,
                GetProp(info, "Description") as string ?? "",
                GetProp(info, "Author") as string ?? "",
                discovered.Version,
                open, fileName, slot, uiEnabled,
                discovered.PackagePath, discovered.ManifestPath));
            snap.ContextsByOwner[moduleName] = alc;

            if (open)
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

            // 5.1.3：Attach 不在扫描期发生。装载阶段只负责把程序集读进来、把反射指令
            // 收进快照；接入宿主与登记指令统一由 AttachPhase 按依赖序逐个进行，
            // 于是任何模块 Attach 时，排在它前面的模块指令都已经可以调用。
            // ✓ / ✗ 那行日志随之后移——「装上了没有」在接上之前还不成立。
            if (!contextAttached)
            {
                snap.PendingAttach.Add(new PendingModule(
                    moduleName,
                    commandPrefix,
                    types,
                    open ? "全暴露" : "精准暴露",
                    GetProp(info, "Version") as string ?? declaredVersion,
                    $"← {(slot.Length > 0 ? slot + "/" : "")}{fileName}"));
                contextAttached = true;
            }
        }
    }

    /// <summary>
    /// 读 manifest 的 <c>pinned</c> 标志：声明本模块**不可热重载**。
    ///
    /// 不走 <see cref="ModuleDiscoveryEntry"/>：那是已冻结的公开记录，为一个标志改它的
    /// 构造函数是破坏性变更；宿主直接读取包声明。
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

}
