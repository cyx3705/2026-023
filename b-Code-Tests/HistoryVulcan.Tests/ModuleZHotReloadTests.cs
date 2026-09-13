using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

// 与 RuntimeModulePackageTests 共用同一串行集合：这些用例都往同一个临时模块目录写包，
// 且 ContextFixture 带进程级静态状态，并行跑会互相看到对方的模块。
[Collection(RuntimeModulePackageCollection.Name)]
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
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
        {
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

}
