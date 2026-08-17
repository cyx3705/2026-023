using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
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
        }

        _mcpPolicy.Unbind();
        _watcher.Dispose();
        var ui = UiContext;
        if (ui != null)
        {
            try
            {
                ui.Send(_ =>
                {
                    DestroyAllTrialUi();
                    DestroyUi(_current);
                    UnregisterCommands(_current);
                }, null);
            }
            catch (Exception ex)
            {
                _log.Warn("module", $"退出时销毁 UI 模块失败: {ex.Message}");
            }
        }
        else
        {
            UnregisterCommands(_current);
        }

        foreach (var alc in _current.Contexts)
            alc.Unload();
        _current = Snapshot.Empty;
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

        public List<(IUiModule Module, string Owner)> UiModules { get; } = new();



        /// <summary>Manifest-declared MCP exposure by module owner for the live snapshot.</summary>
        public Dictionary<string, string?> McpExposures { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>模块元信息(vulcan.module.list);CommandCount 在注册完成后定稿。</summary>
        public List<(string Name, string Desc, string Author, string Version, bool Open, string File,
            string Slot, bool Ui, string? SourcePath, string? ManifestPath)> Metas
        { get; } = new();

        public List<ModuleMeta> Modules { get; } = new();

        private readonly Dictionary<string, int> _commandCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _instances = new();

        public object GetInstance(Type t) => _instances.GetOrAdd(t, x => Activator.CreateInstance(x)!);

        public void CountCommand(string moduleName)
            => _commandCounts[moduleName] = _commandCounts.GetValueOrDefault(moduleName) + 1;

        public void ClearCommandCount(string moduleName) => _commandCounts.Remove(moduleName);

        public void DropInstancesFrom(AssemblyLoadContext alc)
        {
            foreach (var type in _instances.Keys)
            {
                if (ReferenceEquals(AssemblyLoadContext.GetLoadContext(type.Assembly), alc))
                    _instances.TryRemove(type, out _);
            }
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
                    SourcePath = first.SourcePath,
                    ManifestPath = first.ManifestPath,
                });
            }
        }
    }

    private sealed class ModuleContext(
        Snapshot snapshot,
        string owner,
        CommandBus bus,
        ISettingsService settings,
        IShellLog log,
        string dataDirectory) : IModuleContext
    {
        public CommandBus Bus { get; } = bus;

        public ISettingsService Settings { get; } = settings;

        public IShellLog Log { get; } = log;

        public string DataDirectory { get; } = dataDirectory;

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
            : base($"Modules-{DateTime.Now:HHmmssfff}", isCollectible: true) => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 模块目录里有同名 DLL 就从内存加载;否则返回 null 回落到默认上下文(框架程序集)
            var path = Path.Combine(_dir, name.Name + ".dll");
            if (File.Exists(path))
                return LoadFromStream(new MemoryStream(ReadFileWithRetry(path)));
            return null;
        }
    }
}
