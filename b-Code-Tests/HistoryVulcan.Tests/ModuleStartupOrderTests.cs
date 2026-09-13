using System.Security.Cryptography;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests
{
    /// <summary>
    /// 装载次序与就绪屏障（5.1.3）。
    ///
    /// 被守护的缺陷：5.1.2 之前模块在扫描期就接上宿主，而全部模块的指令要等整轮装载
    /// 结束才一次性换进活登记表。于是任何在 <c>Attach</c> 里就开始干活的模块（界面模块
    /// 必然如此）都在一张不完整的目录上工作，缺多少取决于剩余模块的装载耗时——
    /// 表现为「启动时有几率少加载几个模块」。
    /// </summary>
    // 与 RuntimeModulePackageTests 共用同一串行集合：本类会把测试程序集整份复制进模块槽，
    // 并且用进程级环境变量开关夹具。并行跑时别的用例会看到被打开的夹具，凭空多出模块与指令。
    [Collection(RuntimeModulePackageCollection.Name)]
    public sealed class ModuleStartupOrderTests
    {
        [Theory]
        [InlineData("HistoryAlpha", "self-dependency")]
        [InlineData("HistoryMissing", "unknown-dependency")]
        public void SingleModuleStillReportsInvalidDependencies(string dependency, string code)
        {
            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryAlpha" }, name => name, _ => new[] { dependency }, out var problems);
            Assert.Equal(["HistoryAlpha"], ordered);
            Assert.Equal(code, Assert.Single(problems).Code);
        }

        [Fact]
        public void ReadDependsOnAcceptsOnlyNonEmptyStringsAndDeduplicates()
        {
            var root = NewRoot();
            try
            {
                var manifest = Path.Combine(root, "module.manifest.json");
                File.WriteAllText(manifest, """
                    {
                      "schemaVersion": 1,
                      "type": "HistoryVulcan.Module",
                      "name": "HistoryAlpha",
                      "dependsOn": ["HistoryZeta", "  ", "HistoryZeta", 7, "HistoryBeta"]
                    }
                    """);

                Assert.Equal(
                    new[] { "HistoryZeta", "HistoryBeta" },
                    ModuleHost.ReadDependsOn(manifest));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("""{ "name": "X" }""")]
        [InlineData("""{ "name": "X", "dependsOn": "HistoryZeta" }""")]
        [InlineData("not json at all")]
        public void ReadDependsOnTreatsMissingOrMalformedDeclarationsAsNone(string content)
        {
            var root = NewRoot();
            try
            {
                var manifest = Path.Combine(root, "module.manifest.json");
                File.WriteAllText(manifest, content);
                Assert.Empty(ModuleHost.ReadDependsOn(manifest));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ReadDependsOnReturnsNoneForAMissingManifest()
        {
            var root = NewRoot();
            try
            {
                Assert.Empty(ModuleHost.ReadDependsOn(Path.Combine(root, "absent.json")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void OrderWithoutDeclarationsIsTheAlphabeticalBaseline()
        {
            // 没有任何声明时次序必须与 5.1.2 完全一致：升级不该凭空改变既有装载次序。
            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryMercury", "HistoryAurora", "HistoryJanus" },
                name => name,
                _ => Array.Empty<string>(),
                out var problems);

            Assert.Equal(new[] { "HistoryAurora", "HistoryJanus", "HistoryMercury" }, ordered);
            Assert.Empty(problems);
        }

        [Fact]
        public void DeclaredDependenciesAttachBeforeTheirConsumers()
        {
            var declared = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["HistoryAlpha"] = ["HistoryZeta"],
                ["HistoryMercury"] = ["HistoryAlpha"],
            };

            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryAlpha", "HistoryMercury", "HistoryZeta" },
                name => name,
                name => declared.GetValueOrDefault(name, []),
                out var problems);

            Assert.Equal(new[] { "HistoryZeta", "HistoryAlpha", "HistoryMercury" }, ordered);
            Assert.Empty(problems);
        }

        [Fact]
        public void UnknownDependencyIsReportedButNeverDropsTheModule()
        {
            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryAlpha", "HistoryZeta" },
                name => name,
                name => name == "HistoryAlpha" ? ["HistoryAbsent"] : Array.Empty<string>(),
                out var problems);

            // 依赖缺失是模块自己的判断，不是宿主的：少一个模块不该连累别人不装。
            Assert.Equal(new[] { "HistoryAlpha", "HistoryZeta" }, ordered);
            var problem = Assert.Single(problems);
            Assert.Equal("unknown-dependency", problem.Code);
            Assert.Contains("HistoryAbsent", problem.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void DependencyCycleFallsBackToTheBaselineOrderAndSaysSo()
        {
            var declared = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["HistoryAlpha"] = ["HistoryZeta"],
                ["HistoryZeta"] = ["HistoryAlpha"],
            };

            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryAlpha", "HistoryBeta", "HistoryZeta" },
                name => name,
                name => declared.GetValueOrDefault(name, []),
                out var problems);

            // 环内谁先谁后都说不通，但「谁都不装」更坏：一个不少，并且说破。
            Assert.Equal(3, ordered.Count);
            Assert.Equal(
                new[] { "HistoryAlpha", "HistoryBeta", "HistoryZeta" },
                ordered.OrderBy(name => name, StringComparer.Ordinal).ToArray());
            Assert.Equal("dependency-cycle", Assert.Single(problems).Code);
        }

        [Fact]
        public void SelfDependencyIsIgnoredRatherThanStallingTheOrder()
        {
            var ordered = ModuleHost.OrderByDependencies(
                new[] { "HistoryAlpha", "HistoryZeta" },
                name => name,
                name => [name],
                out var problems);

            Assert.Equal(new[] { "HistoryAlpha", "HistoryZeta" }, ordered);
            Assert.All(problems, problem => Assert.Equal("self-dependency", problem.Code));
        }

        [Fact]
        public void AModuleAttachSeesCommandsOfTheModuleItDependsOn()
        {
            // 本轮修复的核心断言。5.1.2 下它必然失败：整轮装载结束之前，
            // 任何模块的指令都不在活登记表里，因此没有任何 Attach 能看见别人的指令。
            var root = NewRoot();
            var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
            var marker = Path.Combine(root, "attach-probe.txt");
            using var fixtures = OrderFixtureScope.Enable(marker);

            // 名称序是 alphafixture 在前；只有按声明的依赖排序，zetafixture 才会先接上。
            CreateOrderPackage(modules, "zetafixture", dependsOn: []);
            CreateOrderPackage(modules, "alphafixture", dependsOn: ["zetafixture"]);

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus, new MemorySettings(), Path.Combine(root, "data"));
                Assert.False(host.IsReady);

                host.Start();

                Assert.True(host.IsReady);
                Assert.Equal(
                    new[] { "zetafixture", "alphafixture" },
                    host.Modules.Select(module => module.ModuleName).ToArray());

                var zeta = host.Modules.Single(module => module.ModuleName == "zetafixture");
                Assert.True(zeta.Attached, string.Join("；", zeta.AttachFailures));
                Assert.Contains("true", File.ReadAllLines(marker));
            }
            finally
            {
                host.Dispose();
                TryDelete(root);
            }
        }

        [Fact]
        public void ReadinessOnlyRisesAfterEveryModuleIsAttached()
        {
            var root = NewRoot();
            var modules = Directory.CreateDirectory(Path.Combine(root, "Modules")).FullName;
            using var fixtures = OrderFixtureScope.Enable(Path.Combine(root, "reload-probe.txt"));

            CreateOrderPackage(modules, "zetafixture", dependsOn: []);

            var registry = new CommandRegistry();
            var log = new RecordingLog();
            var bus = new CommandBus(registry, log);
            var host = new ModuleHost(new RuntimeModuleDiscoverySource(modules), log)
            {
                EnableFileWatching = false,
            };

            try
            {
                host.Attach(registry, bus, new MemorySettings(), Path.Combine(root, "data"));
                host.Start();
                Assert.True(host.IsReady);
                Assert.True(registry.TryGet("zetafixture.ping", out _));

                host.Reload();

                Assert.True(host.IsReady);
                Assert.True(registry.TryGet("zetafixture.ping", out _));
            }
            finally
            {
                host.Dispose();
                Assert.False(host.IsReady);
                TryDelete(root);
            }
        }

        private static string NewRoot()
            => Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"))).FullName;

        private static void TryDelete(string root)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 可回收上下文尚未回收完时磁盘上的 DLL 可能仍被占用；留下临时目录不影响断言。
            }
        }

        /// <summary>建一个带 <c>dependsOn</c> 声明的运行区模块包。</summary>
        private static void CreateOrderPackage(string parent, string name, string[] dependsOn)
        {
            var package = Directory.CreateDirectory(Path.Combine(parent, name)).FullName;
            File.Copy(
                typeof(ZetaFixtureModuleInfo).Assembly.Location,
                Path.Combine(package, "OrderFixture.dll"));
            File.WriteAllText(
                Path.Combine(package, "module.manifest.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    type = "HistoryVulcan.Module",
                    name,
                    version = "v1.0.0",
                    artifact = "OrderFixture.dll",
                    dependsOn,
                    ui = false,
                    pinned = false,
                }));

            var lines = Directory.GetFiles(package, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path =>
                    $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}  "
                    + Path.GetRelativePath(package, path).Replace('\\', '/'));
            File.WriteAllLines(Path.Combine(package, "SHA256SUMS"), lines);
        }

    }

    /// <summary>
    /// 次序夹具的开关。
    ///
    /// 这两个夹具模块默认**关闭**：夹具程序集就是测试程序集本身，别的用例会把它整份
    /// 复制进模块槽。不设开关的话，那些用例会平白多出两个模块，断言随之作废。
    /// </summary>
    public sealed class OrderFixtureScope : IDisposable
    {
        internal const string EnabledVariable = "HISTORYVULCAN_ORDER_FIXTURE";
        internal const string MarkerVariable = "HISTORYVULCAN_ORDER_FIXTURE_MARKER";

        private readonly string? _enabled;
        private readonly string? _marker;

        private OrderFixtureScope(string? enabled, string? marker)
        {
            _enabled = enabled;
            _marker = marker;
        }

        /// <summary>
        /// 夹具是否处于启用状态。
        ///
        /// <c>ModuleInfoBase.Enabled</c> 只挡得住模块身份，挡不住
        /// <see cref="IModuleContextAware"/>：宿主接入的是整个程序集里的实现类，
        /// 与哪个 ModuleInfo 声明了身份无关。别的用例把测试程序集整份复制进模块槽时，
        /// 这两个夹具照样会被 Attach，于是凭空多出一条指令。
        /// </summary>
        internal static bool IsEnabled
            => string.Equals(
                Environment.GetEnvironmentVariable(EnabledVariable), "1", StringComparison.Ordinal);

        internal static OrderFixtureScope Enable(string markerPath)
        {
            var scope = new OrderFixtureScope(
                Environment.GetEnvironmentVariable(EnabledVariable),
                Environment.GetEnvironmentVariable(MarkerVariable));
            Environment.SetEnvironmentVariable(EnabledVariable, "1");
            Environment.SetEnvironmentVariable(MarkerVariable, markerPath);
            return scope;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnabledVariable, _enabled);
            Environment.SetEnvironmentVariable(MarkerVariable, _marker);
        }
    }

    public abstract class OrderFixtureModuleInfo : BaseVariable.ModuleInfoBase
    {
        public override string Version => "v1.0.0";

        public override bool Enabled
            => string.Equals(
                Environment.GetEnvironmentVariable(OrderFixtureScope.EnabledVariable),
                "1",
                StringComparison.Ordinal);
    }

    public sealed class ZetaFixtureModuleInfo : OrderFixtureModuleInfo
    {
        public override string ModuleName => "zetafixture";

        public override Type MainClassType => typeof(ZetaFixture);
    }

    public sealed class AlphaFixtureModuleInfo : OrderFixtureModuleInfo
    {
        public override string ModuleName => "alphafixture";

        public override Type MainClassType => typeof(AlphaFixture);
    }

    /// <summary>被依赖方：在 Attach 里登记一条可被别人调用的指令。</summary>
    public sealed class ZetaFixture : IModuleContextAware
    {
        public void Attach(IModuleContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!OrderFixtureScope.IsEnabled)
                return;

            try
            {
                context.RegisterCommands(registry => registry.Register(new CommandDescriptor
                {
                    Name = "zetafixture.ping",
                    Domain = "zetafixture",
                    CommandClass = "probe",
                    Summary = "Answers so a dependent module can prove it saw this command.",
                    Readonly = true,
                    Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
                }));
            }
            catch (InvalidOperationException)
            {
                // 同一份夹具程序集被两个包各装一次，第二次是重复暂存。
                // 真实模块各有各的程序集，不会走到这里。
            }
        }
    }

    /// <summary>依赖方：在 Attach 里查被依赖方的指令在不在，把答案写进标记文件。</summary>
    public sealed class AlphaFixture : IModuleContextAware
    {
        public void Attach(IModuleContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var marker = Environment.GetEnvironmentVariable(OrderFixtureScope.MarkerVariable);
            if (!OrderFixtureScope.IsEnabled || marker == null)
                return;

            var visible = context.Bus.Registry.TryGet("zetafixture.ping", out _);
            File.AppendAllLines(marker, [visible ? "true" : "false"]);
        }
    }
}
