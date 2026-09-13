using System.Security.Cryptography;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeModulePackageCollection
{
    public const string Name = "runtime-module-packages";
}

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class RuntimeModulePackageTests
{
    [Fact]
    public void DiscoveryRequiresCompleteChecksumsAndRejectsTraversalAndDuplicates()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        CreatePackage(root, "Valid", "contextfixture", "v1.0.0");

        var mismatch = CreatePackage(root, "Mismatch", "mismatch", "v1.0.0");
        File.AppendAllText(Path.Combine(mismatch, "ContextFixture.dll"), "changed");

        var escaped = Directory.CreateDirectory(Path.Combine(root, "Escaped")).FullName;
        File.WriteAllText(Path.Combine(root, "outside.dll"), "outside");
        WriteManifest(escaped, "escaped", "v1.0.0", "../outside.dll");
        WriteChecksums(escaped);

        CreatePackage(root, "DuplicateOne", "duplicate", "v1.0.0");
        CreatePackage(root, "DuplicateTwo", "duplicate", "v1.0.0");

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Equal("contextfixture", Assert.Single(snapshot.Modules).Name);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-checksum");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-manifest");
        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "duplicate-name"));
    }

    [Fact]
    public void RuntimeDataDoesNotInvalidateAnInstalledPackage()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        var package = CreatePackage(root, "HistoryJanus", "HistoryJanus", "v5.4.8");
        var state = Path.Combine(package, "data", "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, "push-history.jsonl"), "runtime state");

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Equal("HistoryJanus", Assert.Single(snapshot.Modules).Name);
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code == "invalid-checksum");
    }

    [Fact]
    public void RuntimeDataCannotContainAnUnverifiedArtifact()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(root, "HistoryJanus")).FullName;
        Directory.CreateDirectory(Path.Combine(package, "data"));
        File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location,
            Path.Combine(package, "data", "HistoryJanus.dll"));
        WriteManifest(package, "HistoryJanus", "v5.4.8", "data/HistoryJanus.dll");
        WriteChecksums(package);

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Empty(snapshot.Modules);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-artifact");
    }

    [Fact]
    public void RollbackDirectoryIsIgnoredInsteadOfMakingTheLivePackageADuplicate()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        CreatePackage(root, "HistoryAurora", "HistoryAurora", "v1.8.17");
        CreatePackage(root, "HistoryAurora-rollback-20260827-102043", "HistoryAurora", "v1.8.16");

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Equal("HistoryAurora", Assert.Single(snapshot.Modules).Name);
        Assert.DoesNotContain(snapshot.Diagnostics, item => item.Code == "duplicate-name");
    }

    [Fact]
    public void InstallIsIdempotentUpgradesAndRemovesValidatedPackages()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0", includeHistory: true);
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            var installed = host.InstallPackage(first);
            Assert.True(installed.Success, installed.Message);
            Assert.Equal("v1.0.0", Assert.Single(host.Modules).Version);
            Assert.False(Directory.Exists(Path.Combine(runtime, "contextfixture", "history")));

            var state = Path.Combine(runtime, "contextfixture", "data", "state");
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "push-history.jsonl"), "keep me");

            var idempotent = host.InstallPackage(first);
            Assert.True(idempotent.Success, idempotent.Message);
            Assert.Contains("无需替换", idempotent.Message, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.Equal("v2.0.0", Assert.Single(host.Modules).Version);
            Assert.Equal("keep me",
                File.ReadAllText(Path.Combine(runtime, "contextfixture", "data", "state", "push-history.jsonl")));

            var removed = host.Uninstall("contextfixture");
            Assert.True(removed.Success, removed.Message);
            Assert.Empty(host.Modules);
            Assert.False(Directory.Exists(Path.Combine(runtime, "contextfixture")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void IdenticalInstallRestoresAnUnloadedModule()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var package = CreatePackage(candidates, "same", "contextfixture", "v1.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();
            Assert.True(host.InstallPackage(package).Success);
            var firstId = Assert.Single(host.Modules).InstanceId;

            Assert.True(host.Unload("contextfixture").Success);
            Assert.Empty(host.Modules);

            var restored = host.InstallPackage(package);
            Assert.True(restored.Success, restored.Message);
            var loaded = Assert.Single(host.Modules);
            Assert.NotEqual(firstId, loaded.InstanceId);
            Assert.True(registry.TryGet("contextfixture.Probe", out _));

            // 「指令回来了」不等于「模块接上了」：卸载残留的待接入条目会让 AttachPhase
            // 用旧类型先接一遍，指令因此照样在，而新实例撞重名后 attached=false。
            // 这条路径与 HotInstallOverALoadedModuleAttachesItExactlyOnce 是同一个不变量的两条入口。
            Assert.True(loaded.Attached, string.Join("；", loaded.AttachFailures));
            Assert.Equal(2, loaded.CommandCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void InstallSwapsOneModuleWithoutTearingDownTheWholeSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(first).Success);
            var firstId = Assert.Single(host.Modules).InstanceId;

            log.Clear();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.DoesNotContain(log.Entries, entry =>
                entry.Message.Contains("先拆除旧界面", StringComparison.Ordinal));
            Assert.Contains(log.Entries, entry =>
                entry.Message.Contains("未拆除其它模块", StringComparison.Ordinal));
            var loaded = Assert.Single(host.Modules);
            Assert.Equal("v2.0.0", loaded.Version);
            Assert.NotEqual(firstId, loaded.InstanceId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void FailedUpgradeRestoresThePreviouslyLoadedPackage()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var good = CreatePackage(candidates, "good", "contextfixture", "v1.0.0");
        var broken = CreatePackage(candidates, "broken", "contextfixture", "v2.0.0", validAssembly: false);
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(good).Success);

            var data = Path.Combine(runtime, "contextfixture", "data", "state.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(data)!);
            File.WriteAllText(data, "keep original state");
            var failed = host.InstallPackage(broken);
            Assert.False(failed.Success);
            Assert.Contains("旧包已恢复", failed.Message, StringComparison.Ordinal);
            Assert.Equal("keep original state", File.ReadAllText(data));
            Assert.Equal("v1.0.0", Assert.Single(host.Modules).Version);
            Assert.Equal(1, host.CurrentContextCount);
            Assert.True(registry.TryGet("contextfixture.Probe", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void LockedRuntimePackageFailsWithoutLosingTheInstalledPackage()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        var log = new RecordingLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Start();
            Assert.True(host.InstallPackage(first).Success);
            var manifest = Path.Combine(runtime, "contextfixture", "module.manifest.json");
            using var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var result = host.InstallPackage(second);
            Assert.False(result.Success);
            Assert.True(File.Exists(manifest));
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.GetDirectoryName(manifest)!, out _, out _, out var error), error);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void OfflineCompositionKeepsUiPackageOnDiskWithoutRequiringInMemorySnapshot()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0", ui: true);
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0", ui: true);
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), new RecordingLog())
        {
            EnableFileWatching = false,
            EnableUiModules = false,
        };

        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Start();
            var installed = host.InstallPackage(first);
            Assert.True(installed.Success, installed.Message);
            Assert.Empty(host.Modules);
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.Combine(runtime, "contextfixture"), out var onDisk, out _, out _));
            Assert.Equal("v1.0.0", onDisk.Version);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.DoesNotContain("未形成后台确认的运行快照", upgraded.Message, StringComparison.Ordinal);
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.Combine(runtime, "contextfixture"), out var upgradedDisk, out _, out _));
            Assert.Equal("v2.0.0", upgradedDisk.Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void InstallingOnePackageDoesNotDropANeighborModulesCommands()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0");
        var neighbor = CreatePackage(candidates, "neighbor", "neighbor", "v1.0.0");
        var upgraded = CreatePackage(candidates, "upgraded", "contextfixture", "v2.0.0");
        var previousVersion = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        var previousName = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, "contextfixture");
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(first).Success);
            Assert.True(registry.TryGet("contextfixture.Probe", out _));

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, "neighbor");
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(neighbor).Success);
            Assert.Equal(2, host.Modules.Count);
            Assert.True(registry.TryGet("contextfixture.Probe", out _));
            Assert.True(registry.TryGet("neighbor.Probe", out _));
            var neighborId = host.Modules.Single(module => module.ModuleName == "neighbor").InstanceId;

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, "contextfixture");
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var result = host.InstallPackage(upgraded);
            Assert.True(result.Success, result.Message);
            Assert.Equal("v2.0.0", host.Modules.Single(module => module.ModuleName == "contextfixture").Version);
            Assert.Equal(neighborId, host.Modules.Single(module => module.ModuleName == "neighbor").InstanceId);
            Assert.True(registry.TryGet("neighbor.Probe", out _));
            Assert.True(registry.TryGet("contextfixture.Probe", out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previousVersion);
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, previousName);
        }
    }

    /// <summary>
    /// 同一进程内「boot → 热装同一模块」必须只接入一次，并且指令数等于新包的实际指令数。
    /// </summary>
    /// <remarks>
    /// 按模块卸载此前不清 <c>PendingAttach</c>：装同名新包时快照里同时留着旧条目和新条目，
    /// <c>AttachPhase(onlyOwner)</c> 于是接两遍——第一遍用已卸载 ALC 的旧类型把**旧**指令面
    /// 注册回去，第二遍的 <c>RegisterCommands</c> 撞在重复暂存检查上抛异常。现场表现是
    /// vulcan.module.list 显示新版本号、旧指令数、attached=false，而 attachFailures 里
    /// 只有一句「重复暂存指令」，看不出是宿主自己接了两遍。重装同一个包也不能自愈：
    /// 内容一致会短路，内容不同则每装一次再多攒一条。
    ///
    /// 因此这里连装三个版本：第二次证明不再接两遍，第三次证明条目不会逐次累积。
    /// </remarks>
    [Fact]
    public void HotInstallOverALoadedModuleAttachesItExactlyOnce()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var third = CreatePackage(candidates, "third", "contextfixture", "v3.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();

            foreach (var (package, version) in new[]
                     {
                         (first, "v1.0.0"),
                         (second, "v2.0.0"),
                         (third, "v3.0.0"),
                     })
            {
                Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, version);
                var installed = host.InstallPackage(package);
                Assert.True(installed.Success, installed.Message);

                var loaded = Assert.Single(host.Modules);
                Assert.Equal(version, loaded.Version);
                Assert.True(
                    loaded.Attached,
                    $"{version} 未接上宿主: {string.Join("；", loaded.AttachFailures)}");

                // 夹具恰好两条：Attach 里显式注册的 context-probe，与反射投影的 Probe。
                // 数错了就说明接入阶段把别的一份指令面也算了进来。
                Assert.Equal(2, loaded.CommandCount);
                Assert.True(registry.TryGet("contextfixture.context-probe", out _));
                Assert.True(registry.TryGet("contextfixture.Probe", out _));
                Assert.Equal(2, registry.All().Count(command =>
                    registry.GetSource(command.Name)
                        .Equals("module:contextfixture", StringComparison.OrdinalIgnoreCase)));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public void FailedStartupPublishesNoCommandsAndIdenticalInstallCanRetry(string failure)
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        var package = CreatePackage(runtime, "contextfixture", "contextfixture", "v1.0.0");
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log) { EnableFileWatching = false };
        var previous = Environment.GetEnvironmentVariable(ContextAwareFixture.FailureVariable);
        try
        {
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, failure);
            host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), temp.Path);
            host.Start();
            Assert.False(Assert.Single(host.Modules).Attached);
            Assert.Empty(registry.All());
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, null);
            var retry = host.InstallPackage(package);
            Assert.True(retry.Success, retry.Message);
            Assert.True(Assert.Single(host.Modules).Attached);
            Assert.Equal(2, registry.All().Count);
        }
        finally { Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, previous); }
    }

    [Fact]
    public void FailedHotAttachRollsBackWithoutPublishingPartialCommands()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        var first = CreatePackage(temp.Path, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(temp.Path, "second", "contextfixture", "v1.0.0");
        File.WriteAllText(Path.Combine(second, "docs", "README.md"), "changed package");
        WriteChecksums(second);
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log) { EnableFileWatching = false };
        var previous = Environment.GetEnvironmentVariable(ContextAwareFixture.FailureVariable);
        var previousData = Environment.GetEnvironmentVariable(ContextAwareFixture.DataVariable);
        var data = Path.Combine(runtime, "contextfixture", "data");
        try
        {
            Environment.SetEnvironmentVariable(ContextAwareFixture.DataVariable, data);
            host.Attach(registry, new CommandBus(registry, log), new MemorySettings(), temp.Path);
            host.Start();
            Assert.True(host.InstallPackage(first).Success);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "state.txt"), "original data");
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, "after-once");
            var failed = host.InstallPackage(second);
            Assert.False(failed.Success);
            Assert.Contains("接入失败", failed.Message, StringComparison.Ordinal);
            Assert.Equal("original data", File.ReadAllText(Path.Combine(data, "state.txt")));
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.Combine(runtime, "contextfixture"), out _, out _, out var validationError), validationError);
            Assert.True(RuntimeModulePackageStore.ChecksumsEqual(first, Path.Combine(runtime, "contextfixture")));
            Assert.Contains("旧包已恢复", failed.Message, StringComparison.Ordinal);
            var module = Assert.Single(host.Modules);
            Assert.Equal("v1.0.0", module.Version);
            Assert.True(module.Attached);
            Assert.Equal(2, registry.All().Count);
        }
        finally
        {
            host.Dispose();
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, previous);
            Environment.SetEnvironmentVariable(ContextAwareFixture.DataVariable, previousData);
        }
    }

    internal static string CreatePackage(
        string parent,
        string directoryName,
        string name,
        string version,
        bool includeHistory = false,
        bool validAssembly = true,
        bool pinned = false,
        bool ui = false)
    {
        var package = Directory.CreateDirectory(Path.Combine(parent, directoryName)).FullName;
        var artifact = Path.Combine(package, "ContextFixture.dll");
        if (validAssembly)
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location, artifact);
        else
            File.WriteAllText(artifact, "not a managed assembly");
        WriteManifest(package, name, version, "ContextFixture.dll", pinned, ui);
        Directory.CreateDirectory(Path.Combine(package, "docs"));
        File.WriteAllText(Path.Combine(package, "docs", "README.md"), $"# {name} {version}");
        if (includeHistory)
        {
            Directory.CreateDirectory(Path.Combine(package, "history", "old"));
            File.WriteAllText(Path.Combine(package, "history", "old", "ignored.txt"), "not payload");
        }
        WriteChecksums(package);
        return package;
    }

    internal static void WriteChecksums(string package)
    {
        var files = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetRelativePath(package, path).Replace('\\', '/')
                .StartsWith("history/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
                $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}  " +
                Path.GetRelativePath(package, path).Replace('\\', '/'));
        File.WriteAllLines(Path.Combine(package, "SHA256SUMS"), files);
    }

    private static void WriteManifest(
        string package,
        string name,
        string version,
        string artifact,
        bool pinned = false,
        bool ui = false)
        => File.WriteAllText(
            Path.Combine(package, "module.manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                type = "HistoryVulcan.Module",
                name,
                version,
                artifact,
                ui,
                pinned,
            }));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "HistoryVulcan.RuntimePackageTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
