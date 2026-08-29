using System.Reflection;
using System.Runtime.Loader;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    /// <summary>
    /// 整体重载：先拆旧界面并卸载可回收 ALC，再装新包。
    /// WPF 模块（例如 HistoryAurora）若先 Build 再卸旧包，进程里会同时存在两份同名
    /// 程序集；默认上下文的 Resolving 钩子会命中旧的那份，新窗口的 XAML 随即解析失败。
    /// </summary>
    public void Reload()
    {
        lock (_reloadLock)
        {
            if (_disposed)
                return;

            var old = _current;
            TeardownSnapshot(old);
            _current = Snapshot.Empty;
            PublishXamlContexts();

            Snapshot next;
            try
            {
                next = Build();
            }
            catch (Exception ex)
            {
                // 拆旧在前是硬约束（同名程序集共存会打断 WPF 的 XAML 解析），所以这里
                // 没有「回滚到旧快照」这个选项——旧的已经卸了。能做的是把宿主停在一个
                // **说得清楚**的状态上：_current 已是 Empty，指令面为空，并且说破原因。
                // 此前这条路径什么都不说，使用者看到的只是所有模块指令一起消失。
                _log.Error("module",
                    $"热重载失败，宿主当前没有装载任何模块；修好模块目录后再执行 vulcan.module.reload。原因: {ex.Message}");
                SyncFileWatching();
                throw;
            }

            CommitSnapshot(old, next);
            SyncFileWatching();
        }

        ReloadCompleted?.Invoke();
    }

    /// <summary>
    /// 只把刚换上的那一个包装进当前快照。不要走 <see cref="Reload"/>：
    /// 整仓拆除会拆掉 Aurora 等其它模块的界面与指令。
    /// 调用方必须已持有 <c>_reloadLock</c>。
    /// </summary>
    private void LoadOne(string packagePath)
    {
        if (!RuntimeModuleDiscoverySource.TryReadPackage(
                packagePath, out var entry, out _, out var error))
        {
            throw new InvalidOperationException($"无法装载刚安装的包: {error}");
        }

        if (ReferenceEquals(_current, Snapshot.Empty))
            _current = new Snapshot();

        var snap = _current;
        var pendingBefore = snap.PendingCommands.Count;
        _building = snap;
        try
        {
            PublishXamlContexts();
            LoadDiscoveredModule(snap, entry);
            CommitAddedCommands(snap, pendingBefore, entry.Name);
        }
        catch
        {
            TeardownAddedModule(snap, entry.Name, pendingBefore);
            throw;
        }
        finally
        {
            _building = null;
            PublishXamlContexts();
        }
    }

    private void CommitAddedCommands(Snapshot snap, int pendingBefore, string moduleName)
    {
        void Commit()
        {
            if (_registry == null)
            {
                snap.ReplaceModuleMeta(moduleName);
                return;
            }

            for (var i = pendingBefore; i < snap.PendingCommands.Count; i++)
            {
                var (descriptor, owner) = snap.PendingCommands[i];
                try
                {
                    var ownedDescriptor = ModuleCommandTaxonomy.Apply(descriptor, owner);
                    _registry.Register(ownedDescriptor, $"module:{owner}");
                    snap.RegisteredNames.Add(descriptor.Name);
                    snap.CountCommand(owner);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
                {
                    _log.Error("module", $"模块 {owner} 的指令 {descriptor.Name} 被拒绝注册: {ex.Message}");
                }
            }

            snap.ReplaceModuleMeta(moduleName);
        }

        var ui = UiContext;
        if (ui == null)
            Commit();
        else
            ui.Send(_ => Commit(), null);

        _log.Info("module", $"已装入模块 {moduleName}，未拆除其它模块");
    }

    private void TeardownAddedModule(Snapshot snap, string moduleName, int pendingBefore)
    {
        if (snap.PendingCommands.Count > pendingBefore)
            snap.PendingCommands.RemoveRange(pendingBefore, snap.PendingCommands.Count - pendingBefore);
        snap.Metas.RemoveAll(meta =>
            meta.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
        if (snap.ContextsByOwner.Remove(moduleName, out var alc)
            && snap.ContextsByOwner.Values.All(remaining => !ReferenceEquals(remaining, alc))
            && !ReferenceEquals(alc, AssemblyLoadContext.Default))
        {
            DisposeInstances(snap.InstancesFrom(alc));
            snap.DropInstancesFrom(alc);
            snap.Contexts.Remove(alc);
            alc.Unload();
        }
    }

    private void TeardownSnapshot(Snapshot old)
    {
        if (old.Modules.Count > 0 || old.Contexts.Count > 0)
            _log.Info("module", "热重载：先拆除旧界面并卸载可回收上下文，再装新包");

        // 必须在 Build / Attach 之前把旧模块指令从活登记表拿掉。
        // HistoryAurora 的 RegisterCommands 看见 live.TryGet 为真就会跳过；
        // 若拆实例后仍留着旧指令，重载后界面命令数会变成 0，且 attachFailures 为空。
        UnregisterSnapshotCommands(old);
        DisposeInstances(old.Instances);

        foreach (var alc in old.Contexts)
            alc.Unload();
    }

    private void UnregisterSnapshotCommands(Snapshot old)
    {
        if (_registry == null)
            return;

        foreach (var name in old.RegisteredNames.ToList())
            _registry.Unregister(name);
    }

    /// <summary>
    /// 回收模块实例持有的进程级资源。
    ///
    /// 必须在 <c>alc.Unload()</c> 之前：卸载后再碰实例就是在动一个已经开始拆的上下文。
    /// 5.0 起宿主不再编排模块界面生命周期；模块自己的窗口由它在总线上登记的命令启动。
    ///
    /// 任何一个实例抛出都只记警告：一个模块拆不干净，不能连累整轮重载——
    /// 那会让宿主停在一个既没有旧快照也没有新快照的状态上。
    /// </summary>
    internal void DisposeInstances(IReadOnlyList<object> instances)
    {
        foreach (var instance in instances)
        {
            if (instance is not IDisposable disposable)
                continue;

            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"回收模块实例 {instance.GetType().FullName} 失败: {ex.Message}");
            }
        }
    }

    private void CommitSnapshot(Snapshot old, Snapshot next)
    {
        void Commit()
        {
            SwapRegistrations(old, next);
            _current = next;
            PublishXamlContexts();
        }

        var ui = UiContext;
        if (ui == null)
            Commit();
        else
            ui.Send(_ => Commit(), null);

        _log.Info("module",
            $"模块装载完成: {next.Modules.Count} 个模块,{next.RegisteredNames.Count} 条指令");
    }

    private void EnsureXamlResolver()
    {
        if (_xamlResolverInstalled)
            return;
        _xamlResolverInstalled = true;
        AssemblyLoadContext.Default.Resolving += ResolveFromModuleContexts;
    }

    private void PublishXamlContexts()
    {
        var building = _building;
        var count = _current.Contexts.Count + (building?.Contexts.Count ?? 0);
        var list = new AssemblyLoadContext[count];
        var index = 0;
        foreach (var alc in _current.Contexts)
            list[index++] = alc;
        if (building != null)
        {
            foreach (var alc in building.Contexts)
                list[index++] = alc;
        }

        Volatile.Write(ref _xamlContexts, list);
    }

    private void TrackContext(Snapshot snap, AssemblyLoadContext alc)
    {
        snap.Contexts.Add(alc);
        PublishXamlContexts();
    }

    /// <summary>
    /// XAML 按程序集简单名走默认上下文。把可回收 ALC 里已装载的同名程序集交回去；
    /// 尚未装载的，只从该 ALC 的模块包目录装进<strong>那个</strong>可回收上下文。
    /// 不要装进 Default——装进去就卸不掉，也锁住磁盘上的 DLL。
    /// 本方法不得取 <c>_reloadLock</c>：Attach / InitializeComponent 就在那把锁里。
    /// 也不得对可回收 ALC 调用 <c>LoadFromAssemblyName</c>：包里没有的程序集会回落到
    /// Default，再进本钩子，无限递归。
    /// </summary>
    private Assembly? ResolveFromModuleContexts(AssemblyLoadContext _, AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name))
            return null;

        var contexts = Volatile.Read(ref _xamlContexts);
        foreach (var alc in contexts)
        {
            foreach (var loaded in alc.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
        }

        foreach (var alc in contexts)
        {
            if (alc is ModuleLoadContext local
                && local.TryLoadFromPackage(name, out var loaded))
                return loaded;
        }

        return null;
    }

    /// <summary>
    /// 把钉住模块的依赖解析到它自己的包目录。
    ///
    /// 默认上下文只探测宿主目录，不认识模块包；没有这个钩子，模块主程序集能装上，
    /// 它引用的 AvalonDock 却找不到。只用于 <c>pinned: true</c>。
    /// </summary>
    private Assembly? ResolvePinnedDependency(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name))
            return null;

        // 读的是不可变快照，不是可变集合本身。
        //
        // 本回调挂在 AssemblyLoadContext.Default.Resolving 上，会在**任意**触发程序集
        // 解析的线程上执行，而且按 ResolveFromModuleContexts 的注释所述不得取 _reloadLock。
        // 装第二个 pinned 模块时 LoadPinned 正在写这份名单，若此处直接遍历一个普通
        // HashSet，就是典型的并发读写：抛「集合已修改」或读到撕裂状态，
        // 表现为随机的依赖解析失败——而失败点离真正的原因很远。
        foreach (var package in Volatile.Read(ref _pinnedPackages))
        {
            var path = Path.Combine(package, name.Name + ".dll");
            if (File.Exists(path))
                return context.LoadFromAssemblyPath(path);
        }

        return null;
    }

    private Assembly LoadPinned(ModuleDiscoveryEntry module)
    {
        var known = Volatile.Read(ref _pinnedPackages);
        if (!known.Contains(module.PackagePath, StringComparer.OrdinalIgnoreCase))
        {
            // 整体替换而不是就地追加：读方拿到的永远是一份完整、此后不再变化的数组。
            // 本方法只在 _reloadLock 内被调用，因此写方之间不会互相竞争。
            Volatile.Write(ref _pinnedPackages, [.. known, module.PackagePath]);

            if (!_pinnedResolverInstalled)
            {
                _pinnedResolverInstalled = true;
                AssemblyLoadContext.Default.Resolving += ResolvePinnedDependency;
            }

            _log.Info("module", $"钉住模块 {module.Name}：装入默认上下文，重载不卸载");
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(module.ArtifactPath);
    }
}
