using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{

    private static CommandDescriptor BuildDescriptor(
        Snapshot snap, string commandName, string moduleName, Type type, MethodInfo method,
        string summary, IReadOnlyDictionary<string, string> paramDocs)
    {
        var parameters = new List<ParameterSpec>();
        var example = commandName;
        var ps = method.GetParameters();
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var paramType = MapType(p.ParameterType);
            parameters.Add(new ParameterSpec
            {
                Name = p.Name ?? $"arg{i}",
                Description = paramDocs.GetValueOrDefault(p.Name ?? "", ""),
                Type = paramType,
                Required = !p.HasDefaultValue,
                Default = p.HasDefaultValue ? DefaultText(p.DefaultValue) : null,
                Position = i,
            });
            example += $" {p.Name}={SampleValue(paramType)}";
        }

        return new CommandDescriptor
        {
            Name = commandName,
            Domain = moduleName,
            // 未声明类时留空，交给注册表按名称结构推导（三段取第二段、两段判为无类）。
            // 固定回退 core 会给两段式直接方法凭空安上一个 core 类。
            CommandClass = method.GetCustomAttribute<ModuleCommandAttribute>()?.CommandClass ?? string.Empty,
            Summary = summary.Length > 0 ? summary : $"{moduleName} 模块 {type.Name}.{method.Name} 方法",
            Example = example,
            Parameters = parameters,
            Readonly = method.GetCustomAttribute<ModuleCommandAttribute>()?.Readonly == true,
            Handler = async ctx =>
            {
                var args = BindArgs(method, ctx);
                var target = method.IsStatic ? null : snap.GetInstance(type);
                object? result;
                try
                {
                    result = method.Invoke(target, args);
                }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    // MD-06:剥掉反射包装,把模块自身异常清晰上抛(总线红字兜底)
                    throw new InvalidOperationException($"模块方法异常: {tie.InnerException.Message}");
                }

                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    result = task.GetType().GetProperty("Result")?.GetValue(task);
                    if (result?.GetType().Name == "VoidTaskResult")
                        result = null;
                }

                return CommandResult.Ok(Render(result), result);
            },
        };
    }

    // ---------------------------------------------------------------- 参数绑定与结果渲染(MD-04)

    private static object?[] BindArgs(MethodInfo method, CommandContext ctx)
    {
        var ps = method.GetParameters();
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            var raw = p.Name != null ? ctx.GetString(p.Name) : null;
            if (raw == null)
            {
                args[i] = p.HasDefaultValue ? p.DefaultValue
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType)
                    : null;
                continue;
            }

            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            try
            {
                args[i] = t == typeof(string) ? raw
                    : t.IsEnum ? Enum.Parse(t, raw, ignoreCase: true)
                    : t == typeof(bool) ? ctx.GetBool(p.Name!)
                    : Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
            }
            catch
            {
                throw new ArgumentException($"参数 {p.Name} 类型转换失败,期望 {t.Name},实际 \"{raw}\"");
            }
        }

        return args;
    }

    private static readonly JsonSerializerOptions RenderOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Render(object? result) => result switch
    {
        null => "(无返回值)",
        string s => s,
        _ when result.GetType().IsPrimitive || result is decimal || result is DateTime
            => Convert.ToString(result, CultureInfo.InvariantCulture) ?? "",
        _ => JsonSerializer.Serialize(result, RenderOpts),
    };

    private static ParamType MapType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte))
            return ParamType.Int;
        if (t == typeof(double) || t == typeof(float) || t == typeof(decimal))
            return ParamType.Double;
        if (t == typeof(bool))
            return ParamType.Bool;
        return ParamType.String;
    }

    private static string SampleValue(ParamType t) => t switch
    {
        ParamType.Int => "1",
        ParamType.Double => "1.5",
        ParamType.Bool => "true",
        _ => "文本",
    };

    private static string? DefaultText(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- 鸭子类型契约(MD-02)

    /// <summary>按基类全名判断,模块编译时引用哪个版本的 BaseVariable.dll 都能识别。</summary>
    private static bool IsModuleInfo(Type t)
    {
        for (var b = t.BaseType; b != null; b = b.BaseType)
        {
            if (b.FullName == "BaseVariable.ModuleInfoBase")
                return true;
        }

        return false;
    }

    private static object? GetProp(object o, string name)
    {
        try
        {
            return o.GetType().GetProperty(name)?.GetValue(o);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>文件可能正在拷贝中,重试读取。</summary>
    internal static byte[] ReadFileWithRetry(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Dispose()
    {
        lock (_reloadLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _ready = false;
        }

        _watcher.Dispose();
        UnregisterCommands(_current);

        // 与热重载同一条拆除次序：界面先拆，再回收实例持有的端口与句柄，最后卸载上下文。
        DisposeInstances(_current.Instances);

        foreach (var alc in _current.Contexts)
            alc.Unload();
        _current = Snapshot.Empty;
        PublishXamlContexts();
        if (_xamlResolverInstalled)
        {
            AssemblyLoadContext.Default.Resolving -= ResolveFromModuleContexts;
            _xamlResolverInstalled = false;
        }

        if (_pinnedResolverInstalled)
        {
            AssemblyLoadContext.Default.Resolving -= ResolvePinnedDependency;
            _pinnedResolverInstalled = false;
        }
    }


    private void UnregisterCommands(Snapshot snapshot)
    {
        if (_registry == null)
            return;

        foreach (var name in snapshot.RegisteredNames)
            _registry.Unregister(name);
        snapshot.RegisteredNames.Clear();
    }


    // ---------------------------------------------------------------- 快照与加载上下文

    /// <summary>一次加载的不可变快照:可卸载 ALC 组(根 + 每槽一个) + 指令表 + 单例缓存。热重载整体替换。</summary>
    private sealed class Snapshot
    {
        public static readonly Snapshot Empty = new();

        /// <summary>本快照持有的全部加载上下文(MH-01:根平铺一个 + 每模块槽一个)。</summary>
        public List<AssemblyLoadContext> Contexts { get; } = new();

        /// <summary>模块 owner → 装载它的 ALC；槽内多模块可共享同一上下文。</summary>
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

        public void ClearCommandCount(string moduleName) => _commandCounts.Remove(moduleName);

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
        /// <remarks>
        /// 「一个模块在快照里留下哪些痕迹」只在这里写一遍。此前按模块卸载
        /// （<c>UnloadFromSnapshot</c>）与单包装载回滚（<c>TeardownAddedModule</c>）
        /// 各自列了一份清单，两份逐渐不一致：卸载那份漏掉了 <see cref="PendingAttach"/>
        /// 与 <see cref="AttachFailures"/>。漏掉的代价不出现在卸载那一刻——
        /// 装同名新包时旧的待接入条目还在，<c>AttachPhase</c> 于是把同一个模块接两遍：
        /// 第一遍用已卸载 ALC 的旧类型把**旧**指令面注册回去，第二遍撞在
        /// <c>ModuleContext.RegisterCommands</c> 的重名检查上抛异常，
        /// 模块最终 attached=false、指令数停在旧值，而重装同一个包也不能自愈。
        ///
        /// 指令表（<see cref="PendingCommands"/> / <see cref="RegisteredNames"/>）
        /// 与 <see cref="Modules"/> 不在这里：前者两个调用方各有取舍（一个按活登记表
        /// 注销、一个按本次装载的区间回滚），后者由注册表消费线程改写，
        /// 不能在未编组的回滚路径上顺手动。
        /// </remarks>
        public AssemblyLoadContext? ForgetModule(string moduleName)
        {
            PendingAttach.RemoveAll(module =>
                module.Owner.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            Metas.RemoveAll(meta =>
                meta.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase));
            AttachFailures.Remove(moduleName);
            ClearCommandCount(moduleName);
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
            foreach (var group in Metas.GroupBy(meta => meta.Name, StringComparer.OrdinalIgnoreCase))
            {
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
        public CommandBus Bus { get; } = bus;

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

    /// <summary>可回收的加载上下文:模块及其依赖全部从内存流加载,不锁磁盘文件。</summary>
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public ModuleLoadContext(string dir)
            : base("Modules-" + DateTime.Now.ToString("HHmmssfff"), isCollectible: true)
            => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 模块目录里有同名 DLL 就从内存加载;否则返回 null 回落到默认上下文(框架程序集)
            return TryLoadFromPackage(name, out var loaded) ? loaded : null;
        }

        /// <summary>
        /// 只从本包目录装载。找不到就返回 false，绝不回落到 Default。
        /// </summary>
        internal bool TryLoadFromPackage(AssemblyName name, out Assembly? assembly)
        {
            assembly = null;
            if (string.IsNullOrEmpty(name.Name))
                return false;

            foreach (var loaded in Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = loaded;
                    return true;
                }
            }

            var path = Path.Combine(_dir, name.Name + ".dll");
            if (!File.Exists(path))
                return false;

            assembly = LoadFromStream(new MemoryStream(ReadFileWithRetry(path)));
            return true;
        }
    }
}
