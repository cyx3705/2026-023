using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 钉住模块（manifest 的 <c>pinned: true</c>）：装进不可回收的装载上下文，重载时不卸载、
/// 不重建上下文。
///
/// 为什么需要这条：模块缺省装进可回收上下文，重载末尾逐个 <c>Unload()</c>；而实测一次冷启动
/// 就会重载 2–3 次（初次装载 + 宿主 moduleRevision 通知引发的确认源重载）。
/// 对初始化了**进程级状态**的模块——例如把 WPF 界面开在宿主进程内的模块——
/// 这等于每次启动都要把类型解析器、Dispatcher 和资源程序集拆掉重来，必然崩在第二次。
///
/// 复用同一个上下文还有一个被依赖的副作用：同名程序集只装载一次，模块的**静态字段跨重载
/// 存活**，模块因此能自己做幂等守卫，宿主不必替它记住"已经初始化过了"。
/// </summary>
// 与 RuntimeModulePackageTests 共用同一串行集合：这些用例都往同一个临时模块目录写包，
// 且 ContextFixture 带进程级静态状态，并行跑会互相看到对方的模块。
[Collection(RuntimeModulePackageCollection.Name)]
public sealed class PinnedModuleTests
{
    [Fact]
    public void PinnedPackageKeepsOneLoadContextAcrossReloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(root, "Modules");
        RuntimeModulePackageTests.CreatePackage(
            modules, "pinnedfixture", "contextfixture", "v1.0.0", pinned: true);

        var log = new TestLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
        {
            EnableUiModules = false,
            EnableFileWatching = false,
        };

        try
        {
            var registry = new CommandRegistry();
            host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), Path.Combine(root, "data"));
            host.Start();
            host.Reload();
            host.Reload();

            // 上下文只在首次创建时打这一行；三轮装载只出现一次，即证明后两轮复用了它。
            Assert.Equal(1, log.Entries.Count(entry => entry.Message.Contains("钉住模块", StringComparison.Ordinal)));
            Assert.Equal("contextfixture", Assert.Single(host.Modules).ModuleName);
        }
        finally
        {
            host.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UnpinnedPackageIsNotPinned()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(root, "Modules");
        RuntimeModulePackageTests.CreatePackage(modules, "plainfixture", "contextfixture", "v1.0.0");

        var log = new TestLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
        {
            EnableUiModules = false,
            EnableFileWatching = false,
        };

        try
        {
            var registry = new CommandRegistry();
            host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), Path.Combine(root, "data"));
            host.Start();
            host.Reload();

            // 缺省仍是可回收上下文：钉住是显式声明的例外，不能因为加了这条机制就悄悄改变缺省。
            Assert.DoesNotContain(log.Entries, entry => entry.Message.Contains("钉住模块", StringComparison.Ordinal));
            Assert.Equal("contextfixture", Assert.Single(host.Modules).ModuleName);
        }
        finally
        {
            host.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.GetValueOrDefault(key);

        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
    }

    private sealed class TestLog : IShellLog
    {
        public List<ShellLogEntry> Entries { get; } = [];

        public void Log(ShellLogLevel level, string category, string message)
            => Entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }

        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    }
}
