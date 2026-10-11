using System.IO.Compression;
using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class ModuleAssemblySelectionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeDllsRemainReadableWithoutInterruptingModuleCommands(bool nativeFirst)
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        const string nativeRelative = "runtimes/win-x64/native/Kernel32.dll";
        var managedRelative = nativeFirst ? "tools/ContextFixture.dll" : "lib/net8.0/ContextFixture.dll";
        var nativeBytes = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "kernel32.dll"));
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [nativeRelative] = nativeBytes,
            [managedRelative] = File.ReadAllBytes(typeof(ContextFixtureModuleInfo).Assembly.Location),
            ["module.manifest.json"] = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,"type":"HistoryVulcan.Module","name":"contextfixture","version":"v1.0.0","ui":false}
                """),
        };
        var package = Path.Combine(runtime, "contextfixture.nupkg");
        ModuleNupkg.PackBytes(package, "contextfixture", "v1.0.0", files);
        using (var archive = ZipFile.OpenRead(package))
        {
            var entries = archive.Entries.Select(entry => entry.FullName).ToList();
            Assert.Equal(nativeFirst, entries.IndexOf(nativeRelative) < entries.IndexOf(managedRelative));
        }

        var variables = new Dictionary<string, string?>
        {
            [ContextFixtureModuleInfo.NameVariable] = "contextfixture",
            [ContextFixtureModuleInfo.VersionVariable] = "v1.0.0",
            [ContextFixtureModuleInfo.EnabledVariable] = null,
            [ContextAwareFixture.FailureVariable] = null,
        };
        var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
            ModuleDataRoot = Path.Combine(temp.Path, "ModuleData"),
        };

        try
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);
            host.Attach(registry, bus);
            host.Start();

            var module = Assert.Single(host.Modules);
            Assert.True(module.Attached, string.Join("; ", module.AttachFailures));
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, module.CommandCount);
            Assert.DoesNotContain(host.DiscoveryDiagnostics, diagnostic => diagnostic.Code == "load-failed");

            var registered = await bus.ExecuteAsync("contextfixture.context-probe", "test");
            Assert.True(registered.Success, registered.Message);
            Assert.Equal("registered", registered.Message);
            var reflected = await bus.ExecuteAsync("contextfixture.Probe", "test");
            Assert.True(reflected.Success, reflected.Message);
            Assert.Equal("reflected", reflected.Message);

            var environment = await bus.ExecuteAsync("contextfixture.context-env", "test");
            Assert.True(environment.Success, environment.Message);
            var content = environment.Message.Split('|')[4];
            Assert.Equal(nativeBytes, File.ReadAllBytes(Path.Combine(content, nativeRelative)));
        }
        finally
        {
            host.Dispose();
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
