using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
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

        var infos = new List<object>();
        foreach (var infoType in infoTypes)
        {
            try
            {
                infos.Add(Activator.CreateInstance(infoType)!);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"实例化 {infoType.FullName} 失败: {ex.Message}");
            }
        }
        var identityMatches = infos.Any(info =>
            string.Equals(GetProp(info, "ModuleName") as string, discovered.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(GetProp(info, "Version") as string, discovered.Version, StringComparison.OrdinalIgnoreCase));
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

        foreach (var info in infos)
        {
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
