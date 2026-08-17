using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ModuleZHotReloadTests
{
    [Fact]
    public async Task RuntimeHostReloadsOnceWhenPackageDllChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        var modules = Path.Combine(root, "Modules");
        var package = RuntimeModulePackageTests.CreatePackage(
            modules, "contextfixture", "contextfixture", "v1.0.0");

        var dllPath = Path.Combine(package, "ContextFixture.dll");
        var registry = new CommandRegistry();
        var log = new TestLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
        {
            EnableUiModules = false,
            EnableFileWatching = true,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(root, "data"));
            host.Start();
            Assert.Equal("contextfixture", Assert.Single(host.Modules).ModuleName);
            Assert.Contains(log.Entries, entry =>
                entry.Message.Contains("正在监听模块目录", StringComparison.Ordinal));

            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reloads = 0;
            host.ReloadCompleted += () =>
            {
                if (Interlocked.Increment(ref reloads) == 1)
                    pending.TrySetResult(true);
            };
            File.WriteAllBytes(dllPath, File.ReadAllBytes(dllPath));

            var completed = await Task.WhenAny(pending.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.True(completed == pending.Task, "Runtime package change should hot-reload without restarting the host.");
            await Task.Delay(1200);
            Assert.Equal(1, Volatile.Read(ref reloads));
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

        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;

        public void Set(string key, string value) => _values[key] = value;

        public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
    }

    private sealed class TestLog : IShellLog
    {
        public List<ShellLogEntry> Entries { get; } = [];

        public void Log(ShellLogLevel level, string category, string message)
            => Entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded
        {
            add { }
            remove { }
        }

        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    }
}
