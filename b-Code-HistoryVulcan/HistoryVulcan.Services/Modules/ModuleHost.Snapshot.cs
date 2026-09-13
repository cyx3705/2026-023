using System.Runtime.Loader;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    // ---------------------------------------------------------------- 快照与加载上下文

    /// <summary>一次装载持有的上下文、命令与实例；整轮重载时替换，单包安装时按所有者更新。</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();

        /// <summary>本快照持有的可回收加载上下文。</summary>
        public List<AssemblyLoadContext> Contexts { get; } = new();

        /// <summary>模块 owner → 装载它的 ALC。</summary>
        public Dictionary<string, AssemblyLoadContext> ContextsByOwner { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>扫描出的待注册指令(注册在 UI 线程完成,冲突者被剔除)。</summary>
        public List<(CommandDescriptor Descriptor, string ModuleName)> PendingCommands { get; } = new();

        /// <summary>实际注册成功的指令名(热重载时按此注销)。</summary>
        public List<string> RegisteredNames { get; } = new();

        /// <summary>模块 owner → 上下文注入失败原因；非空即表示该模块没有真正接上宿主。</summary>
        public Dictionary<string, List<string>> AttachFailures { get; }
            = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 待接入模块，按本轮装载次序排列（5.1.3）。
        ///
        /// 装载阶段只把程序集读进来并收集反射指令；接入与指令登记留到
        /// <c>AttachPhase</c> 逐个进行，次序由 manifest 的 <c>dependsOn</c> 决定。
        /// </summary>
        public List<PendingModule> PendingAttach { get; } = new();

        /// <summary>模块元信息(vulcan.module.list);CommandCount 在注册完成后定稿。</summary>
        public List<(string Name, string Desc, string Author, string Version, bool Open, string File,
            string Slot, bool Ui, string? SourcePath, string? ManifestPath)> Metas
        { get; } = new();

        public List<ModuleMeta> Modules { get; } = new();

        private readonly Dictionary<string, int> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _instances = new();

        public object GetInstance(Type t) => _instances.GetOrAdd(t, x => Activator.CreateInstance(x)!);

        /// <summary>
        /// 本快照构造出的全部模块实例，供拆除阶段回收持有的进程级资源。
        ///
        /// 卸载 ALC 只回收托管内存，不会关闭实例持有的操作系统句柄：端口、文件锁、
        /// 命名管道、计时器都不在垃圾回收的管辖范围内。非界面模块此前没有任何拆除回调，
        /// 于是这类资源在每轮热重载后被静默遗弃——旧监听器仍占着端口、仍持有活的
        /// 指令总线引用，而新实例只能退到下一个端口。
        /// </summary>
        public IReadOnlyList<object> Instances => _instances.Values.ToArray();

        public void CountCommand(string moduleName)
            => _commandCounts[moduleName] = _commandCounts.GetValueOrDefault(moduleName) + 1;

        /// <summary>取出由指定加载上下文装载的全部实例，供按模块卸载时回收资源。</summary>
        public IReadOnlyList<object> InstancesFrom(AssemblyLoadContext alc)
            => _instances
                .Where(pair => ReferenceEquals(
                    AssemblyLoadContext.GetLoadContext(pair.Key.Assembly), alc))
                .Select(pair => pair.Value)
                .ToArray();

        public void DropInstancesFrom(AssemblyLoadContext alc)
        {
            foreach (var type in _instances.Keys)
            {
                if (ReferenceEquals(AssemblyLoadContext.GetLoadContext(type.Assembly), alc))
                    _instances.TryRemove(type, out _);
            }
        }

        /// <summary>
        /// 抹掉一个模块在本快照里的按模块登记，并交出装载它的上下文（没有则为 null）。
        /// </summary>
        public AssemblyLoadContext? ForgetModule(string moduleName)
        {
            PendingAttach.RemoveAll(module =>
                module.Owner.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            Metas.RemoveAll(meta =>
                meta.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            AttachFailures.Remove(moduleName);
            _commandCounts.Remove(moduleName);
            ContextsByOwner.Remove(moduleName, out var alc);
            return alc;
        }

        public void ReplaceModuleMeta(string moduleName)
        {
            Modules.RemoveAll(module =>
                module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            var group = Metas
                .Where(meta => meta.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (group.Count == 0)
                return;
            var first = group.First();
            Modules.Add(new ModuleMeta(
                first.Name,
                string.Join("; ", group.Select(meta => meta.Desc).Where(value => value.Length > 0)),
                first.Author,
                first.Version,
                group.Any(meta => meta.Open),
                first.File,
                _commandCounts.GetValueOrDefault(first.Name),
                first.Slot,
                group.Any(meta => meta.Ui))
            {
                InstanceId = Guid.NewGuid().ToString("N"),
                SourcePath = first.SourcePath,
                ManifestPath = first.ManifestPath,
                AttachFailures = AttachFailures.GetValueOrDefault(first.Name) is { } reasons
                    ? reasons.ToArray()
                    : [],
            });
        }

        public void FinalizeMetas()
        {
            Modules.Clear();
            foreach (var name in Metas.Select(meta => meta.Name).Distinct(StringComparer.OrdinalIgnoreCase))
                ReplaceModuleMeta(name);
        }

    }

    /// <summary>
    /// 一个已装载、尚未接入宿主的模块。
    ///
    /// <c>Types</c> 是该程序集的类型表，接入阶段据此找 <c>IModuleContextAware</c> 实现；
    /// 其余字段只为接入完成后那一行 ✓ / ✗ 日志——它必须在接入之后打印，
    /// 因为「装上了没有」这句话在接上之前还不成立。
    /// </summary>
    private sealed record PendingModule(
        string Owner,
        string CommandPrefix,
        IReadOnlyList<Type> Types,
        string Exposure,
        string Version,
        string Origin);

    private sealed class ModuleContext(
        Snapshot snapshot,
        string owner,
        CommandBus bus) : IModuleContext
    {
        public CommandBus Bus => bus;

        public IDisposable RegisterFrontend(IFrontend frontend) => bus.ClaimFrontend(owner, frontend);

        public void RegisterCommands(Action<CommandRegistry> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var staging = new CommandRegistry();
            configure(staging);

            foreach (var descriptor in staging.All())
            {
                if (snapshot.PendingCommands.Any(item =>
                        item.Descriptor.Name.Equals(descriptor.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"模块 {owner} 重复暂存指令: {descriptor.Name}");
                }

                snapshot.PendingCommands.Add((descriptor, owner));
            }
        }
    }

}
