using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 钉住模块（manifest 的 <c>pinned: true</c>）：装进不可回收的装载上下文，重载时不卸载、
/// 不重建上下文。缺省模块仍走可回收 ALC；WPF 壳（HistoryAurora）已改为先拆界面再装新包，
/// 不再使用本标志。本机制留给真正不能拆进程级状态的模块。
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

            log.Entries.Clear();
            host.Reload();
            var teardown = log.Entries.FindIndex(entry =>
                entry.Message.Contains("先拆除旧界面", StringComparison.Ordinal));
            var loaded = log.Entries.FindIndex(entry =>
                entry.Message.Contains("模块装载完成", StringComparison.Ordinal));
            Assert.True(teardown >= 0, "可回收模块重载必须先拆旧包");
            Assert.True(loaded > teardown, "拆完旧包之后才能装新包");
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
