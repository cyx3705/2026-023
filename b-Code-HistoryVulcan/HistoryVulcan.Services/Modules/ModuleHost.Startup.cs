using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 装载次序与就绪屏障（5.1.3）。
///
/// 5.1.2 之前，一轮装载是「扫描时逐个 <c>Attach</c>，最后一次性把全部指令换进活登记表」。
/// 两件事之间隔着整整一轮装载：先接上的模块已经在跑，而它要调用的其它模块的指令
/// 一条都还没登记。模块自己开线程（界面模块必然如此）时，这段窗口的长短取决于
/// 剩余模块的装载耗时，于是同一台机器上「有时全都在、有时少几个」——
/// 日志里留下的是成片的 <c>未知指令: aurora.ui.actions</c> 和只建了一半的页面。
///
/// 5.1.3 把这条时间线改成两段，并给出一个可被询问、可被通知的完成点：
/// <list type="number">
/// <item>装载阶段只读程序集、收指令，不接入任何模块；</item>
/// <item>接入阶段按**声明的依赖序**逐个 <c>Attach</c>，每接上一个就立刻把它的指令
/// 登记进活登记表——于是任何模块 <c>Attach</c> 时，排在它前面的模块指令都已可调用；</item>
/// <item>全部接上后置 <see cref="IsReady"/>，并按命令名通知声明了就绪钩子的模块。</item>
/// </list>
///
/// 依赖写在模块自己的 <c>module.manifest.json</c> 里（<c>dependsOn</c>），与
/// <c>ui</c> / <c>pinned</c> 同一模式由宿主直读：<see cref="ModuleDiscoveryEntry"/>
/// 是已定版记录，为一个可选字段改它的构造函数是破坏性变更。
/// </summary>
public sealed partial class ModuleHost
{
    /// <summary>模块可选声明的就绪钩子后缀：<c>&lt;域&gt;.host.ready</c>。</summary>
    internal const string ReadyHookSuffix = ".host.ready";

    /// <summary>就绪通知的来源标签；走安静通道，不进指令历史。</summary>
    internal const string ReadySource = "host:module-ready";

    /// <summary>manifest 里声明模块级前置依赖的字段名。</summary>
    internal const string DependsOnField = "dependsOn";

    private volatile bool _ready;

    /// <summary>
    /// 当前快照是否已完成「全部模块接上宿主、指令登记完毕」。
    ///
    /// 装载中为 false。这是 <c>vulcan.module.ready</c> 的唯一真值：模块与远端据此
    /// 判断现在看到的指令面是不是完整的，而不必靠「等一会儿再试」猜。
    /// </summary>
    internal bool IsReady => _ready;

    /// <summary>
    /// 读 manifest 的 <c>dependsOn</c>：本模块要求先接上的其它模块名。
    ///
    /// 只影响**接入次序**，不构成硬前置：依赖缺失时本模块照样装载，只是记一条发现诊断。
    /// 宿主不替模块决定「少了谁就不能跑」——那是模块自己的判断，而把它做成硬失败会让
    /// 一个模块的缺席连累整棵依赖树。
    /// </summary>
    internal static IReadOnlyList<string> ReadDependsOn(string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            return [];

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty(DependsOnField, out var value)
                || value.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// 按声明的依赖关系排出接入次序：依赖在前，其余保持名称序。
    ///
    /// 名称序是**基准序**而不是兜底：没有任何声明时结果与 5.1.2 完全一致，
    /// 升级不会凭空改变既有次序；有声明时只把必要的模块前移，其余仍然稳定。
    /// 未知依赖与依赖环都不失败——它们各记一条诊断，剩余模块按基准序补齐，
    /// 因为「排不出次序」不该等于「谁都不装」。
    /// </summary>
    internal static IReadOnlyList<T> OrderByDependencies<T>(
        IReadOnlyList<T> items,
        Func<T, string> nameOf,
        Func<T, IReadOnlyList<string>> dependenciesOf,
        out IReadOnlyList<ModuleOrderProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(nameOf);
        ArgumentNullException.ThrowIfNull(dependenciesOf);

        var found = new List<ModuleOrderProblem>();
        problems = found;
        if (items.Count == 0)
            return items;

        var baseline = items.OrderBy(nameOf, StringComparer.OrdinalIgnoreCase).ToList();
        var known = baseline.ToDictionary(nameOf, item => item, StringComparer.OrdinalIgnoreCase);

        var waiting = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in baseline)
        {
            var owner = nameOf(item);
            var required = new List<string>();
            foreach (var dependency in dependenciesOf(item))
            {
                if (dependency.Equals(owner, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new ModuleOrderProblem(
                        owner, "self-dependency", $"{owner} 的 dependsOn 指向自己，已忽略。"));
                    continue;
                }

                if (known.ContainsKey(dependency))
                    required.Add(dependency);
                else
                    found.Add(new ModuleOrderProblem(
                        owner,
                        "unknown-dependency",
                        $"{owner} 声明依赖 {dependency}，运行区没有这个模块；次序按其余依赖排。"));
            }

            waiting[owner] = required;
        }

        var ordered = new List<T>(baseline.Count);
        var settled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var remaining = new List<T>(baseline);
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(item =>
                waiting[nameOf(item)].All(settled.Contains));
            if (next == null)
            {
                // 环：谁先谁后都说不通，但「谁都不装」更坏。按基准序补齐并说破。
                found.Add(new ModuleOrderProblem(
                    string.Join("、", remaining.Select(nameOf)),
                    "dependency-cycle",
                    "dependsOn 构成环，环内模块按名称序接入；请在模块 manifest 里断开环。"));
                ordered.AddRange(remaining);
                break;
            }

            ordered.Add(next);
            settled.Add(nameOf(next));
            remaining.Remove(next);
        }

        return ordered;
    }

    /// <summary>按 manifest 的 dependsOn 排出本轮装载次序，并把排序问题记进发现诊断。</summary>
    private IReadOnlyList<ModuleDiscoveryEntry> OrderForStartup(
        IReadOnlyList<ModuleDiscoveryEntry> modules)
    {
        var declared = modules.ToDictionary(
            module => module.Name,
            module => ReadDependsOn(module.ManifestPath),
            StringComparer.OrdinalIgnoreCase);

        var ordered = OrderByDependencies(
            modules,
            module => module.Name,
            module => declared[module.Name],
            out var problems);

        foreach (var problem in problems)
        {
            _discoveryDiagnostics =
            [
                .. _discoveryDiagnostics,
                new ModuleDiscoveryDiagnostic(problem.Module, problem.Code, problem.Message),
            ];
            _log.Warn("module.discovery", $"[{problem.Code}] {problem.Message}");
        }

        if (declared.Values.Any(list => list.Count > 0))
            _log.Info("module", $"按声明依赖排定接入次序: {string.Join(" → ", ordered.Select(module => module.Name))}");

        return ordered;
    }

    /// <summary>
    /// 接入阶段：按装载次序逐个接上宿主，每接上一个立刻登记它的指令。
    ///
    /// 逐个登记是这次修复的关键。一次性登记意味着「全部接完之前，谁的指令都不在」，
    /// 而模块的 <c>Attach</c> 恰恰是它开始干活的地方；逐个登记则把这段窗口收敛成
    /// 「只看不见排在我后面的模块」，而那正是 <c>dependsOn</c> 能表达的事。
    /// </summary>
    /// <param name="snap">当前已提交的快照。</param>
    /// <param name="onlyOwner">只接入这一个模块（单包热装路径）；null 表示整轮。</param>
    private void AttachPhase(Snapshot snap, string? onlyOwner = null)
    {
        foreach (var module in snap.PendingAttach.ToList())
        {
            if (onlyOwner != null
                && !module.Owner.Equals(onlyOwner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AttachModuleContexts(snap, module.Types, module.Owner);
            MarshalToUi(() =>
            {
                if (!snap.AttachFailures.ContainsKey(module.Owner))
                    RegisterModuleCommands(snap, module.Owner);
                snap.ReplaceModuleMeta(module.Owner);
            });

            var failures = snap.AttachFailures.GetValueOrDefault(module.Owner);
            if (failures is { Count: > 0 })
            {
                // 接不上宿主就是没装上。打 ✓ 会让 vulcan.module.list 显示模块在位、
                // 版本正确、0 条指令，而原因只在日志里。
                _log.Error("module",
                    $"✗ 模块 {module.Owner} {module.Version} 未接上宿主，指令不会注册 {module.Origin}"
                    + Environment.NewLine + "    "
                    + string.Join(Environment.NewLine + "    ", failures));
            }
            else
            {
                _log.Info("module", $"✓ 模块 {module.Owner} {module.Version} ({module.Exposure}) {module.Origin}");
            }
        }
    }

    /// <summary>
    /// 通知声明了就绪钩子的模块：本轮装载已经完整。
    ///
    /// 界面模块在 <c>Attach</c> 里就开了自己的线程，它拉目录的时刻不由宿主决定，
    /// 因此仅靠次序修不好——必须有一个「现在可以了」的信号。钩子是可选的：
    /// 没声明就跳过，宿主不因为模块没实现它而改变任何行为。
    ///
    /// 走安静通道并且离开 <c>_reloadLock</c> 之后再发：钩子里回调宿主（重载、
    /// 装包、编组到 UI 线程）是完全正常的写法，在锁内同步发就会把它变成死锁。
    /// </summary>
    private void AnnounceReady()
    {
        var bus = _bus;
        var registry = _registry;
        if (bus == null || registry == null)
            return;

        // 钩子名按**指令域**拼（HistoryAurora → aurora.host.ready），这是文档写给模块的形状。
        // 同时试一次模块自报的 CommandPrefix：反射指令用的是它，两种约定在现网都存在，
        // 只认一种就会让另一半模块的钩子永远不被调用，而且不报错——那是最难查的一类沉默。
        var hooks = _current.PendingAttach
            .Where(module => snapshotAttached(module.Owner))
            .SelectMany(module => new[]
            {
                ModuleDomainNaming.ToDomain(module.Owner) + ReadyHookSuffix,
                module.CommandPrefix + ReadyHookSuffix,
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(name => registry.TryGet(name, out _))
            .ToList();
        if (hooks.Count == 0)
            return;

        _ = Task.Run(async () =>
        {
            foreach (var hook in hooks)
            {
                try
                {
                    var result = await bus.InvokeAsync(hook, ReadySource).ConfigureAwait(false);
                    if (!result.Success)
                        _log.Warn("module", $"就绪通知 {hook} 返回失败: {result.Message}");
                }
                catch (Exception ex)
                {
                    // 一个模块的就绪回调抛异常，不该拦住排在它后面的模块收到通知。
                    _log.Warn("module", $"就绪通知 {hook} 异常: {ex.GetType().Name}: {ex.Message}");
                }
            }

            _log.Info("module", $"已通知 {hooks.Count} 个模块：宿主装载完成");
        });

        bool snapshotAttached(string owner)
            => _current.Modules.Any(meta =>
                meta.ModuleName.Equals(owner, StringComparison.OrdinalIgnoreCase) && meta.Attached);
    }

    /// <summary>把回调编组到注册表消费线程；未装配 UI 上下文时就地执行。</summary>
    private void MarshalToUi(Action action)
    {
        var ui = UiContext;
        if (ui == null)
            action();
        else
            ui.Send(_ => action(), null);
    }
}

/// <summary>装载次序无法按声明排定时留下的说明；不构成失败。</summary>
internal sealed record ModuleOrderProblem(string Module, string Code, string Message);
