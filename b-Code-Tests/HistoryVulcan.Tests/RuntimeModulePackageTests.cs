using System.Security.Cryptography;
using System.Text;
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
    public void DiscoveryAcceptsNupkgAndRejectsCorruptDuplicatesAndLooseFiles()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        CreatePackage(root, "Valid", "contextfixture", "v1.0.0");

        var mismatch = CreatePackage(root, "Mismatch", "mismatch", "v1.0.0");
        File.WriteAllText(mismatch, "changed");

        File.WriteAllText(Path.Combine(root, "outside.dll"), "outside");
        var escaped = Directory.CreateDirectory(Path.Combine(root, "Escaped")).FullName;
        File.WriteAllText(Path.Combine(escaped, "module.manifest.json"), """{"schemaVersion":1}""");

        CreatePackage(root, "DuplicateOne", "duplicate", "v1.0.0");
        CreatePackage(root, "DuplicateTwo", "duplicate", "v1.0.0");

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Equal("contextfixture", Assert.Single(snapshot.Modules).Name);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-package");
        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "duplicate-name"));
        Assert.DoesNotContain(snapshot.Modules, item => item.Name == "escaped");
    }

    [Fact]
    public void FilesBesideThePackageAreNotPartOfIt()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        var package = CreatePackage(root, "HistoryJanus", "HistoryJanus", "v5.4.8");
        File.WriteAllText(Path.Combine(root, "push-history.jsonl"), "runtime state");

        var snapshot = new RuntimeModuleDiscoverySource(root).Discover();

        Assert.Equal(package, Assert.Single(snapshot.Modules).PackagePath);
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
            host.Attach(registry, bus);
            host.Start();

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            var installed = host.InstallPackage(first);
            Assert.True(installed.Success, installed.Message);
            Assert.Equal("v1.0.0", Assert.Single(host.Modules).Version);
            Assert.True(File.Exists(Path.Combine(runtime, "contextfixture.nupkg")));
            Assert.False(Directory.Exists(Path.Combine(runtime, "contextfixture")));

            var idempotent = host.InstallPackage(first);
            Assert.True(idempotent.Success, idempotent.Message);
            Assert.Contains("无需替换", idempotent.Message, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.Equal("v2.0.0", Assert.Single(host.Modules).Version);

            var removed = host.Uninstall("contextfixture");
            Assert.True(removed.Success, removed.Message);
            Assert.Empty(host.Modules);
            Assert.False(File.Exists(Path.Combine(runtime, "contextfixture.nupkg")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public void TargetOwnedByAnotherModuleKeepsItsLoadedSnapshotAndCommands()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        var original = CreatePackage(runtime, "Foo", "contextfixture", "v1.0.0");
        var incoming = CreatePackage(temp.Path, "incoming", "Foo", "v1.0.0");
        var expected = File.ReadAllBytes(original);
        var previousName = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable);
        var previousVersion = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log) { EnableFileWatching = false };
        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, "contextfixture");
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Attach(registry, new CommandBus(registry, log));
            host.Start();
            var loaded = Assert.Single(host.Modules);
            Assert.True(loaded.Attached);
            Assert.True(registry.TryGet("contextfixture.Probe", out var command));

            var rejected = host.InstallPackage(incoming);

            Assert.False(rejected.Success);
            Assert.Contains("contextfixture", rejected.Message, StringComparison.Ordinal);
            Assert.Equal(expected, File.ReadAllBytes(original));
            var retainedModule = Assert.Single(host.Modules);
            Assert.Equal(loaded.InstanceId, retainedModule.InstanceId);
            Assert.Equal(loaded, retainedModule);
            Assert.True(retainedModule.Attached);
            Assert.True(registry.TryGet("contextfixture.Probe", out var retainedCommand));
            Assert.Same(command, retainedCommand);
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, registry.All().Count);
            Assert.False(Directory.Exists(Path.Combine(temp.Path, ".module-transactions")));
        }
        finally
        {
            host.Dispose();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable, previousName);
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previousVersion);
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
            host.Attach(registry, bus);
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
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, loaded.CommandCount);
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
            host.Attach(registry, bus);
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
            host.Attach(registry, bus);
            host.Start();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(good).Success);

            var failed = host.InstallPackage(broken);
            Assert.False(failed.Success);
            Assert.Contains("旧包已恢复", failed.Message, StringComparison.Ordinal);
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
            var installed = Path.Combine(runtime, "contextfixture.nupkg");
            using var locked = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var result = host.InstallPackage(second);
            Assert.False(result.Success);
            Assert.True(File.Exists(installed));
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                installed, out _, out _, out var error), error);
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
                Path.Combine(runtime, "contextfixture.nupkg"), out var onDisk, out _, out _));
            Assert.Equal("v1.0.0", onDisk.Version);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.DoesNotContain("未形成后台确认的运行快照", upgraded.Message, StringComparison.Ordinal);
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.Combine(runtime, "contextfixture.nupkg"), out var upgradedDisk, out _, out _));
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
            host.Attach(registry, bus);
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
            host.Attach(registry, bus);
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

                // 夹具指令数见 ContextFixtureModuleInfo.CommandCount（Attach 显式登记的几条加反射投影的 Probe）。
                // 数错了就说明接入阶段把别的一份指令面也算了进来。
                Assert.Equal(ContextFixtureModuleInfo.CommandCount, loaded.CommandCount);
                Assert.True(registry.TryGet("contextfixture.context-probe", out _));
                Assert.True(registry.TryGet("contextfixture.Probe", out _));
                Assert.Equal(ContextFixtureModuleInfo.CommandCount, registry.All().Count(command =>
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
            host.Attach(registry, new CommandBus(registry, log));
            host.Start();
            Assert.False(Assert.Single(host.Modules).Attached);
            Assert.Empty(registry.All());
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, null);
            var retry = host.InstallPackage(package);
            Assert.True(retry.Success, retry.Message);
            Assert.True(Assert.Single(host.Modules).Attached);
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, registry.All().Count);
        }
        finally { Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, previous); }
    }

    [Fact]
    public void FailedHotAttachRollsBackWithoutPublishingPartialCommands()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        var first = CreatePackage(temp.Path, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(temp.Path, "second", "contextfixture", "v1.0.0", marker: "changed");
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log) { EnableFileWatching = false };
        var previous = Environment.GetEnvironmentVariable(ContextAwareFixture.FailureVariable);
        var previousData = Environment.GetEnvironmentVariable(ContextAwareFixture.DataVariable);
        var data = Path.Combine(temp.Path, "ModuleData", "contextfixture");
        try
        {
            Environment.SetEnvironmentVariable(ContextAwareFixture.DataVariable, data);
            host.Attach(registry, new CommandBus(registry, log));
            host.Start();
            Assert.True(host.InstallPackage(first).Success);
            Directory.CreateDirectory(data);
            File.WriteAllText(Path.Combine(data, "state.txt"), "original data");
            Environment.SetEnvironmentVariable(ContextAwareFixture.FailureVariable, "after-once");
            var failed = host.InstallPackage(second);
            Assert.False(failed.Success);
            Assert.Contains("接入失败", failed.Message, StringComparison.Ordinal);
            // 模块数据归模块：宿主只回滚包，不替模块回滚 ModuleData 里的内容（6.0.0 起没有槽位 data/）。
            Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(
                Path.Combine(runtime, "contextfixture.nupkg"), out _, out _, out var validationError), validationError);
            Assert.True(RuntimeModulePackageStore.ChecksumsEqual(first, Path.Combine(runtime, "contextfixture.nupkg")));
            Assert.Contains("旧包已恢复", failed.Message, StringComparison.Ordinal);
            var module = Assert.Single(host.Modules);
            Assert.Equal("v1.0.0", module.Version);
            Assert.True(module.Attached);
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, registry.All().Count);
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
        bool ui = false,
        string marker = "")
    {
        var nupkg = Path.Combine(parent, directoryName + ".nupkg");
        var dll = validAssembly
            ? File.ReadAllBytes(typeof(ContextFixtureModuleInfo).Assembly.Location)
            : "not a managed assembly"u8.ToArray();
        var manifest = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            type = "HistoryVulcan.Module",
            name,
            version,
            artifact = "ContextFixture.dll",
            ui,
            pinned,
        });
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ContextFixture.dll"] = dll,
            ["module.manifest.json"] = Encoding.UTF8.GetBytes(manifest),
            ["docs/README.md"] = Encoding.UTF8.GetBytes($"# {name} {version}\n{marker}"),
        };
        if (includeHistory)
            files["history/old/ignored.txt"] = "not payload"u8.ToArray();
        ModuleNupkg.PackBytes(nupkg, name, version, files);
        return nupkg;
    }

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
