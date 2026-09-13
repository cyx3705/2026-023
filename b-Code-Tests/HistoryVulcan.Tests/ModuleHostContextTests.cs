using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests
{
    // 与 RuntimeModulePackageTests 共用同一串行集合：本类同样把测试程序集复制进模块槽，
    // 并用进程级环境变量控制夹具身份。此前它不在任何集合里，靠「只有它开这些开关」侥幸成立；
    // 5.1.3 加入第二组夹具后这条侥幸不再成立，装载结果开始随并行调度漂移。
    [Collection(RuntimeModulePackageCollection.Name)]
    public sealed class ModuleHostContextTests
    {
        [Fact]
        public void UnloadedContextCanBeCollectedWhileTheHostRemainsAlive()
        {
            using var temp = new TemporaryDirectory();
            var (host, unloaded) = UnloadAndObserve(temp.Path);
            using (host)
            {
                for (var attempt = 0; attempt < 10 && unloaded.IsAlive; attempt++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                Assert.False(unloaded.IsAlive);
                GC.KeepAlive(host);
            }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static (ModuleHost Host, WeakReference Context) UnloadAndObserve(string root)
        {
            RuntimeModulePackageTests.CreatePackage(root, "contextfixture", "contextfixture", "v1.0.0");
            var before = System.Runtime.Loader.AssemblyLoadContext.All.ToHashSet();
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(root), new NullLog()) { EnableFileWatching = false };
            var registry = new CommandRegistry();
            host.Attach(registry, new CommandBus(registry, new NullLog()));
            host.Start();
            var loaded = System.Runtime.Loader.AssemblyLoadContext.All.Single(context => context.IsCollectible && !before.Contains(context));
            var reference = new WeakReference(loaded);
            Assert.True(host.Unload("contextfixture").Success);
            return (host, reference);
        }

        [Fact]
        public void ModuleMetadataIsConstructedOncePerScan()
        {
            using var temp = new TemporaryDirectory();
            var marker = Path.Combine(temp.Path, "metadata-constructions.txt");
            var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.ConstructionVariable);
            try
            {
                Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.ConstructionVariable, marker);
                RuntimeModulePackageTests.CreatePackage(temp.Path, "contextfixture", "contextfixture", "v1.0.0");
                using var host = new ModuleHost(new RuntimeModuleDiscoverySource(temp.Path), new NullLog())
                {
                    EnableFileWatching = false,
                };
                var registry = new CommandRegistry();
                host.Attach(registry, new CommandBus(registry, new NullLog()));
                host.Start();
                Assert.Single(host.Modules);
                Assert.Single(File.ReadAllLines(marker));
            }
            finally { Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.ConstructionVariable, previous); }
        }

        [Fact]
        public void EmptyRuntimeDirectoryStartsWithoutModules()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            Directory.CreateDirectory(modulesDirectory);
            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            host.Attach(registry, bus);
            host.Start();

            try
            {
                Assert.Empty(host.Modules);
                Assert.Equal(0, host.CurrentContextCount);
            }
            finally
            {
                host.Dispose();
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task ContextAwareModuleRegistersCommandsOnlyThroughTheBus()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(slotDirectory);
            Directory.CreateDirectory(dataDirectory);

            RuntimeModulePackageTests.CreatePackage(modulesDirectory, "context-fixture", "contextfixture", "v1.0.0");

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus);
                host.Start();

                Assert.True(registry.TryGet("contextfixture.context-probe", out var direct));
                Assert.True(registry.TryGet("contextfixture.Probe", out var reflected));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));
                Assert.Equal("contextfixture", registry.GetDomain(direct.Name));
                Assert.Equal("context", registry.GetCommandClass(direct.Name));
                Assert.Equal("contextfixture", registry.GetDomain(reflected.Name));
                Assert.Equal("probe", registry.GetCommandClass(reflected.Name));

                var result = await bus.ExecuteAsync("contextfixture.context-probe", "test");

                Assert.True(result.Success, result.Message);
                Assert.Equal("registered", result.Message);
                // 恰好两条：一条显式注册、一条反射投影。
                // Attach 与 Dispose 都是生命周期契约的实现，不得成为指令——
                // 尤其是 Dispose：远端调用它等于拆掉半个模块。
                Assert.False(registry.TryGet("contextfixture.Dispose", out _));
                Assert.Equal(2, Assert.Single(host.Modules).CommandCount);
            }
            finally
            {
                host.Dispose();
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public async Task UiCommandsFailClearlyWhenNoUiModuleIsLoaded()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            Directory.CreateDirectory(modulesDirectory);
            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };
            host.Attach(registry, bus);
            host.Start();

            try
            {
                foreach (var name in new[] { "vulcan.ui.show", "vulcan.ui.hide", "vulcan.ui.dock" })
                {
                    Assert.False(registry.TryGet(name, out _), name);
                    var result = await bus.ExecuteAsync(name, "test");
                    Assert.False(result.Success);
                    Assert.Contains("未知指令", result.Message, StringComparison.Ordinal);
                    Assert.Contains(name, result.Message, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        /// <summary>
        /// 模块实例持有的进程级资源必须在拆除时交还，**三条拆除路径一条都不能漏**。
        /// </summary>
        /// <remarks>
        /// 卸载加载上下文只回收托管内存；端口、文件锁、命名管道、计时器都不在
        /// 垃圾回收的管辖范围内。非界面模块没有 <c>DestroyUi</c> 可依赖，
        /// <c>IDisposable</c> 是它唯一的交还时机。
        ///
        /// 这条不变量在真实系统里连续破了两次，两次都是"少覆盖了一条路径"：
        /// 先是整快照重载没有回收，后是按模块卸载（<c>vulcan.module.install</c> 与
        /// <c>remove</c> 的必经之路）只丢引用不 Dispose。两次的现场一样——
        /// 旧网关继续监听、继续持有活的指令总线引用，新实例只能退到下一个端口。
        ///
        /// 因此本用例按路径逐条断言，而不是笼统测一次"拆除后被 Dispose 了"。
        /// </remarks>
        [Theory]
        [InlineData(TeardownPath.HostDispose)]
        [InlineData(TeardownPath.PerModuleUnload)]
        [InlineData(TeardownPath.FullReload)]
        public void ModuleInstancesAreDisposedOnEveryTeardownPath(TeardownPath path)
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var dataDirectory = Path.Combine(root, "data");
            Directory.CreateDirectory(slotDirectory);
            Directory.CreateDirectory(dataDirectory);
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, "context-fixture", "contextfixture", "v1.0.0");

            var marker = Path.Combine(Path.GetFullPath(dataDirectory), ContextAwareFixture.DisposeMarker);
            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                Environment.SetEnvironmentVariable(ContextAwareFixture.DataVariable, Path.GetFullPath(dataDirectory));
                host.Attach(registry, new CommandBus(registry, log));
                host.Start();
                Assert.False(File.Exists(marker), "装载阶段不应触发拆除");

                switch (path)
                {
                    case TeardownPath.PerModuleUnload:
                        Assert.True(host.Unload("contextfixture").Success);
                        break;
                    case TeardownPath.HostDispose:
                        host.Dispose();
                        break;
                    case TeardownPath.FullReload:
                        // Start 走的就是 Reload：旧快照整体拆除，再装新的。
                        host.Start();
                        break;
                }

                Assert.True(File.Exists(marker), $"{path} 未回收模块实例");
            }
            finally
            {
                host.Dispose();
                try { Directory.Delete(root, recursive: true); }
                catch (IOException) { }
            }
        }

        [Fact]
        public void DisabledModuleDoesNotAttachContextOrRegisterCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, "context-fixture", "contextfixture", "v1.0.0");

            var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable);
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, "0");
            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus);
                host.Start();

                Assert.Empty(host.Modules);
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Assert.False(registry.TryGet("contextfixture.Attach", out _));
            }
            finally
            {
                Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.EnabledVariable, previous);
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void RollbackSlotsAreIgnoredByModuleDiscovery()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var activeDirectory = Path.Combine(modulesDirectory, "context-fixture");
            var rollbackDirectory = Path.Combine(modulesDirectory, "context-fixture-rollback-20260808-000000");
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, Path.GetFileName(activeDirectory), "contextfixture", "v1.0.0");
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, Path.GetFileName(rollbackDirectory), "contextfixture", "v1.0.0");

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, new CommandBus(registry, log));
                host.Start();

                var module = Assert.Single(host.Modules);
                Assert.Equal("contextfixture", module.ModuleName);
                // 回滚槽若被发现，同名指令撞名被拒、计数对不上；接入后登记的上下文指令也计入。
                Assert.Equal(module.CommandCount, registry.All().Count);
            }
            finally
            {
                host.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void UnloadRemovesTheModuleAndItsCommandsWithoutReloading()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, "context-fixture", "contextfixture", "v1.0.0");

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            using var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus);
                host.Start();
                Assert.Equal("contextfixture", Assert.Single(host.Modules).ModuleName);
                Assert.True(registry.TryGet("contextfixture.Probe", out _));

                var missing = host.Unload("HistoryJanus");
                Assert.False(missing.Success);
                Assert.Contains("没有已装载的模块", missing.Message, StringComparison.Ordinal);

                var empty = host.Unload("  ");
                Assert.False(empty.Success);
                Assert.Contains("需要 name", empty.Message, StringComparison.Ordinal);

                var unloaded = host.Unload("contextfixture");
                Assert.True(unloaded.Success, unloaded.Message);
                Assert.Empty(host.Modules);
                Assert.False(registry.TryGet("contextfixture.Probe", out _));
                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.False(host.Unload("contextfixture").Success);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DisposedHostDoesNotReloadOrReattachCommands()
        {
            var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
            var modulesDirectory = Path.Combine(root, "modules");
            var slotDirectory = Path.Combine(modulesDirectory, "context-fixture");
            Directory.CreateDirectory(slotDirectory);
            RuntimeModulePackageTests.CreatePackage(modulesDirectory, "context-fixture", "contextfixture", "v1.0.0");

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var settings = new MemorySettings();
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(modulesDirectory), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus);
                host.Start();
                Assert.True(registry.TryGet("contextfixture.context-probe", out _));

                host.Dispose();
                host.Reload();
                host.Dispose();

                Assert.False(registry.TryGet("contextfixture.context-probe", out _));
                Assert.Empty(host.Modules);
            }
            finally
            {
                host.Dispose();
                Directory.Delete(root, recursive: true);
            }
        }

    }

    /// <summary>宿主拆除模块的路径。整快照重载走 Reload，另两条见此。</summary>
    public enum TeardownPath
    {
        /// <summary>宿主退出：ModuleHost.Dispose。</summary>
        HostDispose,

        /// <summary>按模块卸载：vulcan.module.unload / install / remove 的必经之路。</summary>
        PerModuleUnload,

        /// <summary>整快照热重载：vulcan.module.reload。装包走 LoadOne，不走这条。</summary>
        FullReload,
    }

    public sealed class ContextFixtureModuleInfo : BaseVariable.ModuleInfoBase
    {
        public const string ConstructionVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_CONSTRUCTION";

        public ContextFixtureModuleInfo()
        {
            if (Environment.GetEnvironmentVariable(ConstructionVariable) is { Length: > 0 } path)
                File.AppendAllText(path, "constructed" + Environment.NewLine);
        }

        public const string EnabledVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_ENABLED";
        public const string VersionVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_VERSION";
        public const string NameVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_NAME";

        public override string ModuleName
            => Environment.GetEnvironmentVariable(NameVariable) ?? "contextfixture";

        public override string Version
            => Environment.GetEnvironmentVariable(VersionVariable) ?? "v1.0.0";

        public override Type MainClassType => typeof(ContextAwareFixture);

        public override bool Enabled
            => !string.Equals(Environment.GetEnvironmentVariable(EnabledVariable), "0", StringComparison.Ordinal);
    }

    public sealed class ContextAwareFixture : IModuleContextAware, IDisposable
    {
        /// <summary>
        /// 拆除留痕。用文件而不是静态计数器：夹具程序集被复制进模块槽后由可回收
        /// 加载上下文装载，它的类型标识与测试进程里的那一份不是同一个，
        /// 静态字段互不可见。文件是唯一能跨上下文观测的证据。
        /// </summary>
        public const string DisposeMarker = "fixture-disposed.marker";

        public const string DataVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_DATA";
        public const string FailureVariable = "HISTORYVULCAN_CONTEXT_FIXTURE_FAILURE";

        private string? _dataDirectory;

        public void Attach(IModuleContext context)
        {
            _dataDirectory = Environment.GetEnvironmentVariable(DataVariable);
            var failure = Environment.GetEnvironmentVariable(FailureVariable);
            if (failure == "before")
                throw new InvalidOperationException("fixture attach failed before registration");
            context.RegisterCommands(registry => registry.Register(new CommandDescriptor
            {
                Name = (Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.NameVariable) ?? "contextfixture") + ".context-probe",
                Domain = "spoofed-domain",
                CommandClass = "context",
                Summary = "Proves RegisterCommands staged a command owned by this module.",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("registered")),
            }));
            if (failure is "after" or "after-once")
            {
                if (failure == "after-once")
                {
                    Environment.SetEnvironmentVariable(FailureVariable, null);
                    if (_dataDirectory != null)
                        File.WriteAllText(Path.Combine(_dataDirectory, "state.txt"), "new instance changed data");
                }
                throw new InvalidOperationException("fixture attach failed after registration");
            }
        }

        [ModuleCommand(CommandClass = "probe")]
        public string Probe() => "reflected";

        public void Dispose()
        {
            if (_dataDirectory == null)
                return;
            File.WriteAllText(Path.Combine(_dataDirectory, DisposeMarker), "disposed");
        }
    }

}

// 夹具此前在此内联定义 BaseVariable.ModuleInfoBase 的第六份副本。
// 契约的权威定义已进入 HistoryVulcan.Core(Modules/ModuleInfoBase.cs),
// 本测试直接消费它 —— 夹具与宿主装载逻辑校验的因此是同一个类型。
