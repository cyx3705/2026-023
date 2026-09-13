using System.Reflection;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    /// <summary>取程序集的类型表;缺失依赖只丢掉受影响的类型,不让整个模块下线(MD-06)。</summary>
    private static Type[] LoadTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type != null).ToArray()!;
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
            if (_bus == null)
            {
                _log.Warn("module",
                    $"模块 {owner} 请求宿主总线，但当前装配点只提供了命令注册表；已跳过 {contextType.FullName}");
                continue;
            }

            try
            {
                var module = (IModuleContextAware)snap.GetInstance(contextType);
                module.Attach(new ModuleContext(snap, owner, _bus));
            }
            catch (Exception ex)
            {
                // 记进快照而不只是打条日志：接不上宿主的模块不能被报成装载成功，
                // 判定要能被 vulcan.module.list 读到，而不是只留在日志里。
                // 接入失败的模块若已登记前端，登记不能留下：确认与生命周期中继会打到半初始化的界面。
                _bus.ReleaseFrontend(owner);
                var reason = $"{contextType.FullName}: {ex.GetType().Name}: {ex.Message}";
                _log.Error("module", $"注入模块上下文失败 ({owner}) {reason}");
                if (!snap.AttachFailures.TryGetValue(owner, out var failures))
                    snap.AttachFailures[owner] = failures = [];
                failures.Add(reason);
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
        foreach (var method in type.GetMethods(
                     BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName || method.IsGenericMethodDefinition || IsModuleLifecycleMethod(type, method))
                continue;

            var commandName = $"{commandPrefix}.{method.Name}";
            if (snap.PendingCommands.Any(pending =>
                    pending.Descriptor.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase)))
            {
                _log.Warn("module", $"模块 {moduleName} 内方法重名,跳过 {type.Name}.{method.Name}");
                continue;
            }

            var (summary, paramDocs) = docs?.ForMethod(ns, type.Name, method.Name) ?? ("", EmptyDocs);
            snap.PendingCommands.Add((
                BuildDescriptor(snap, commandName, moduleName, type, method, summary, paramDocs),
                moduleName));
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDocs = new Dictionary<string, string>();

    private static bool IsModuleLifecycleMethod(Type type, MethodInfo method)
    {
        // 生命周期契约的实现方法不是指令：它们由宿主在装载/拆除时调用，
        // 不该同时出现在指令目录里让人（或 agent）手动触发。
        //
        // IDisposable 自 4.3.0 起也算：那一版让宿主在拆除阶段回收模块实例，
        // 于是「实现 IDisposable」从模块的私事变成了与宿主的约定。
        // 不排除的话，任何一个持有资源、因而实现了 IDisposable 的模块，
        // 都会平白多出一条 <域>.dispose 指令——远端调用它等于拆掉半个模块，
        // 而模块作者完全不知道自己暴露了它。
        Type[] lifecycleContracts =
        [
            typeof(IModuleContextAware),
            typeof(IDisposable),
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
}
