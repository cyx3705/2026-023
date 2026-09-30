using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 宿主发布的四个总线主题（5.9.0，DEC-070）。消费方以前直接挂 C# 事件
/// （<c>Registry.Changed</c>、<c>Bus.Executed</c>、<c>IShellLog.EntryAdded</c>），现在一律订阅主题。
/// </summary>
/// <remarks>
/// <para>
/// <c>vulcan.catalog.changed</c> 与 <c>vulcan.module.changed</c> 去抖后发：一轮装载会登记上百条指令，
/// 逐条通知只会让订阅方重拉上百次目录。模块清单按「名字 + 版本 + 是否接上 + 指令数」比对，
/// 于是整轮重载、单包安装、卸载、移除都经同一条路径通知，不必在每条装卸路径上各补一次。
/// </para>
/// <para>
/// <c>vulcan.command.executed</c> 只带指令名、来源、成败与一行提要，不带指令全文与 Data：
/// 全文可能含密钥参数（例如 <c>apollo.key.set</c>），Data 可能很大。
/// </para>
/// </remarks>
internal sealed class HostEventPublisher : IDisposable
{
    internal const string CatalogChanged = "vulcan.catalog.changed";
    internal const string ModuleChanged = "vulcan.module.changed";
    internal const string CommandExecuted = "vulcan.command.executed";
    internal const string LogEntry = "vulcan.log.entry";
    private const string HostSource = "host";

    private readonly CommandRegistry _registry;
    private readonly CommandBus _bus;
    private readonly IShellLog _log;
    private readonly ModuleHost? _modules;
    private readonly Timer _debounce;
    private readonly TimeSpan _delay;
    private long _publishedRevision = -1;
    private string _publishedModules = "";
    private int _disposed;

    public HostEventPublisher(CommandRegistry registry, CommandBus bus, IShellLog log, ModuleHost? modules,
        TimeSpan? debounce = null)
    {
        _registry = registry;
        _bus = bus;
        _log = log;
        _modules = modules;
        _delay = debounce ?? TimeSpan.FromMilliseconds(300);
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        _registry.Changed += Schedule;
        _bus.Executed += OnExecuted;
        _log.EntryAdded += OnLogEntry;
        if (_modules != null)
            _modules.ReloadCompleted += Schedule;
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) == 0)
            _debounce.Change(_delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>立即比对并发布（去抖到点时调用；测试也可直接调用）。</summary>
    internal void Flush()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var revision = _registry.Revision;
        if (Interlocked.Exchange(ref _publishedRevision, revision) != revision)
            _bus.Events.Publish(CatalogChanged, HostSource, new { revision });

        if (_modules == null)
            return;
        var modules = _modules.Modules
            .OrderBy(module => module.ModuleName, StringComparer.OrdinalIgnoreCase)
            .Select(module => new
            {
                name = module.ModuleName,
                version = module.Version,
                attached = module.Attached,
                commandCount = module.CommandCount,
            })
            .ToList();
        var signature = string.Join("|", modules.Select(m => $"{m.name}:{m.version}:{m.attached}:{m.commandCount}"));
        if (!string.Equals(Interlocked.Exchange(ref _publishedModules, signature), signature, StringComparison.Ordinal))
            _bus.Events.Publish(ModuleChanged, HostSource, new { modules });
    }

    private void OnExecuted(string text, string source, CommandResult result)
    {
        var name = (text ?? "").Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var summary = (result.Message ?? "").Split('\n', 2)[0].Trim();
        if (summary.Length > 200)
            summary = summary[..200];
        _bus.Events.Publish(CommandExecuted, HostSource, new { name, source, success = result.Success, summary });
    }

    private void OnLogEntry(object? sender, ShellLogEntry entry)
    {
        // 事件中枢自己的诊断不转发，否则「处理器出错 → 记日志 → 再投递」会绕圈。
        if (entry.Category.Equals(BusEventHub.LogCategory, StringComparison.Ordinal))
            return;
        _bus.Events.Publish(LogEntry, HostSource, new
        {
            time = entry.Time,
            level = entry.Level.ToString().ToLowerInvariant(),
            category = entry.Category,
            message = entry.Message,
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _registry.Changed -= Schedule;
        _bus.Executed -= OnExecuted;
        _log.EntryAdded -= OnLogEntry;
        if (_modules != null)
            _modules.ReloadCompleted -= Schedule;
        _debounce.Dispose();
    }
}
