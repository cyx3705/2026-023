using System.Reflection;
using System.Runtime.Loader;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Extensibility.Modules;

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

            var next = Build();
            CommitSnapshot(old, next);
            SyncFileWatching();
        }

        ReloadCompleted?.Invoke();
    }

    private void TeardownSnapshot(Snapshot old)
    {
        if (old.Modules.Count > 0 || old.Contexts.Count > 0)
            _log.Info("module", "热重载：先拆除旧界面并卸载可回收上下文，再装新包");

        var ui = UiContext;
        if (ui == null)
        {
            DestroyUi(old);
            ClearShellHooks();
        }
        else
        {
            ui.Send(_ =>
            {
                DestroyUi(old);
                ClearShellHooks();
            }, null);
        }

        DisposeInstances(old.Instances);

        foreach (var alc in old.Contexts)
            alc.Unload();
    }

    /// <summary>
    /// 回收模块实例持有的进程级资源。
    ///
    /// 必须在 <c>alc.Unload()</c> 之前：卸载后再碰实例就是在动一个已经开始拆的上下文。
    /// 也必须在 <see cref="DestroyUi"/> 之后：界面拆除要先编组回界面线程，
    /// 而这里可能阻塞在关端口、Join 线程上，顺序颠倒会让界面线程等在一个 IO 上。
    ///
    /// 界面模块的 <c>DestroyUi</c> 与这里的 <c>Dispose</c> 都会被调用，因此
    /// 两者的实现必须各自可重入且互不假设对方已经跑过。
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

    /// <summary>
    /// 丢弃上一轮界面留下的两个反向挂钩。
    ///
    /// 两个都必须清：它们指向的是已经拆掉的那套界面对象。留着任何一个，
    /// 下一轮的消费方就会挂到一个死界面上——注册进去的页面永远不会出现，
    /// 而调用方拿到的是非 null，看不出有什么不对。
    /// </summary>
    private void ClearShellHooks()
    {
        ShellUi = null;
        CommandWorkbench = null;
    }

    private void CommitSnapshot(Snapshot old, Snapshot next)
    {
        // 先 FinalizeMetas / 切 _current，再 CreateUi。界面模块（Aurora）若在
        // Attach 阶段就 Show，模块页 Loaded 会读到 Snapshot.Empty，而且只读一次。
        void Commit()
        {
            SwapRegistrations(old, next);
            _current = next;
            PublishXamlContexts();
            CreateUi(next);
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
        foreach (var package in _pinnedPackages)
        {
            var path = Path.Combine(package, name.Name + ".dll");
            if (File.Exists(path))
                return context.LoadFromAssemblyPath(path);
        }

        return null;
    }

    private Assembly LoadPinned(ModuleDiscoveryEntry module)
    {
        if (_pinnedPackages.Add(module.PackagePath))
        {
            if (!_pinnedResolverInstalled)
            {
                _pinnedResolverInstalled = true;
                AssemblyLoadContext.Default.Resolving += ResolvePinnedDependency;
            }

            _log.Info("module", $"钉住模块 {module.Name}：装入默认上下文，重载不卸载");
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(module.ArtifactPath);
    }

    private void CreateUi(Snapshot snapshot)
    {
        // 第一遍只取提供方。模块装载顺序不可控，不能指望承载界面的那个恰好排在前面。
        // 注册器与命令工作台是两条独立的反向通道：同一个模块通常两条都提供，
        // 但不能因为取到了注册器就 break——那样工作台会永远留在 null 上。
        foreach (var (module, _) in snapshot.UiModules)
        {
            if (ShellUi == null
                && module is IShellUiProvider provider
                && provider.ShellUi != null)
            {
                ShellUi = provider.ShellUi;
                _log.Info("module", $"界面注册器由 {module.GetType().Assembly.GetName().Name} 提供");
            }

            if (CommandWorkbench == null
                && module is IShellCommandWorkbenchProvider workbenchProvider
                && workbenchProvider.CommandWorkbench != null)
            {
                CommandWorkbench = workbenchProvider.CommandWorkbench;
                _log.Info("module", $"命令工作台挂载点由 {module.GetType().Assembly.GetName().Name} 提供");
            }

            if (ShellUi != null && CommandWorkbench != null)
                break;
        }

        // 没有注册器就没有可注册的地方：跳过而不是让每个模块各自撞空引用。
        // 这也保持了「未安装界面模块时宿主纯无头」的行为不变。
        if (ShellUi == null)
            return;

        // 提供方最后 CreateUi：Aurora 在这里才 Show 主窗口。先让其余模块把页面
        // 注册进停靠布局，模块管理页 Loaded 时清单已经定稿。
        var others = snapshot.UiModules.Where(item => item.Module is not IShellUiProvider);
        var providers = snapshot.UiModules.Where(item => item.Module is IShellUiProvider);
        foreach (var item in others.Concat(providers))
        {
            try
            {
                if (item.Module is IShellUiAware aware)
                    aware.ShellUi = ShellUi;

                // 与注册器同理必须在这里再注入一次：装载时那一次发生在 Build 阶段，
                // 而提供方自己也在同一次 Build 里，先装到的模块只能拿到 null。
                if (item.Module is IShellCommandWorkbenchAware workbenchAware)
                    workbenchAware.CommandWorkbench = CommandWorkbench;

                // 必须编组到界面线程。宿主自己的循环不是 STA，模块一建控件就抛
                // "调用线程必须为 STA"——而这条异常被按模块吞掉，表现为某个模块的页面
                // 悄悄少了一个，别的模块照常。注册器知道界面线程在哪，交给它。
                ShellUi.Invoke(item.Module.CreateUi);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"创建 UI 模块 {item.Module.GetType().FullName} 失败: {ex.Message}");
            }
        }
    }

    private void DestroyUi(Snapshot snapshot)
    {
        var others = new List<(IUiModule Module, string Owner)>();
        var providers = new List<(IUiModule Module, string Owner)>();
        foreach (var item in snapshot.UiModules)
        {
            if (item.Module is IShellUiProvider)
                providers.Add(item);
            else
                others.Add(item);
        }

        foreach (var item in others)
            DestroyUiModule(item.Module, item.Owner, marshalToShell: true);

        // 提供方最后拆：它关掉 STA Dispatcher。先 Unregister 再 Destroy，
        // 好让注销仍能编组到还活着的界面线程。提供方自己的 DestroyUi 不得再走
        // ShellUi.Invoke，否则它在界面线程上 Join 自己会自锁。
        foreach (var item in providers)
        {
            try
            {
                ShellUi?.UnregisterOwner(item.Owner);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"回收模块界面失败 ({item.Owner}): {ex.Message}");
            }

            DestroyUiModule(item.Module, item.Owner, marshalToShell: false);
        }
    }

    private void DestroyUiModule(IUiModule module, string owner, bool marshalToShell)
    {
        try
        {
            if (marshalToShell && ShellUi != null)
                ShellUi.Invoke(module.DestroyUi);
            else
                module.DestroyUi();
        }
        catch (Exception ex)
        {
            _log.Warn("module", $"销毁 UI 模块 {module.GetType().FullName} 失败: {ex.Message}");
        }

        if (!marshalToShell)
            return;

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
