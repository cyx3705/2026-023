using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class DemoModuleSampleTests
{
    [Fact]
    public async Task StarterModuleLoadsAsACompletePackageAndRegistersBusCommands()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        var runtime = Path.Combine(root, "modules");
        Directory.CreateDirectory(runtime);
        CreateStarterPackage(Path.Combine(runtime, "DemoModule"));

        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
        };

        try
        {
            host.Attach(registry, bus);
            host.Start();

            var module = Assert.Single(host.Modules);
            Assert.Equal("DemoModule", module.ModuleName);
            Assert.Equal("1.0.0", module.Version);
            Assert.True(module.Attached);
            Assert.Equal(3, module.CommandCount);

            Assert.True(registry.TryGet("demomodule.calc.add", out var add));
            Assert.Equal("demomodule", registry.GetDomain(add.Name));
            Assert.Equal("calc", registry.GetCommandClass(add.Name));
            Assert.Equal("true", add.Annotation("ui.button"));
            Assert.True(registry.TryGet("demomodule.calc.reverse", out _));
            Assert.True(registry.TryGet("demomodule.host.ready", out var ready));
            Assert.False(string.IsNullOrWhiteSpace(ready.HiddenReason));
            Assert.False(registry.TryGet("DemoModule.Add", out _));
            Assert.False(registry.TryGet("demomodule.Add", out _));

            var sum = await bus.ExecuteAsync("demomodule.calc.add a=2 b=3", "test");
            Assert.True(sum.Success, sum.Message);
            Assert.Equal("5", sum.Message);

            var reversed = await bus.ExecuteAsync("demomodule.calc.reverse text=OneHistory", "test");
            Assert.True(reversed.Success, reversed.Message);
            Assert.Equal("yrotsiHenO", reversed.Message);
        }
        finally
        {
            host.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateStarterPackage(string package)
    {
        Directory.CreateDirectory(package);
        var assemblyDir = Path.GetDirectoryName(typeof(DemoModule.ModuleInfo).Assembly.Location)
            ?? throw new InvalidOperationException("找不到 DemoModule 输出目录。");
        foreach (var file in new[] { "DemoModule.dll", "DemoModule.xml" })
        {
            var from = Path.Combine(assemblyDir, file);
            Assert.True(File.Exists(from), $"起步示例缺少 {file}");
            File.Copy(from, Path.Combine(package, file));
        }

        var sample = Path.Combine(RepositoryPaths.Root(), "b-Code-Samples", "DemoModule");
        File.Copy(
            Path.Combine(sample, "module.manifest.json"),
            Path.Combine(package, "module.manifest.json"));
        Directory.CreateDirectory(Path.Combine(package, "docs"));
        File.Copy(
            Path.Combine(sample, "docs", "README.md"),
            Path.Combine(package, "docs", "README.md"));
        RuntimeModulePackageTests.WriteChecksums(package);
        Assert.False(File.Exists(Path.Combine(package, "HistoryVulcan.Core.dll")));
    }

}
