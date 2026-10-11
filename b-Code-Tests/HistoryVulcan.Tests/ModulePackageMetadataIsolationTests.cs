using System.IO.Compression;
using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class ModulePackageMetadataIsolationTests
{
    [Theory]
    [InlineData("broken.nuspec", "<package>", "invalid-package")]
    [InlineData("module.manifest.json", "{", "invalid-manifest")]
    [InlineData("module.manifest.json", "[]", "invalid-manifest")]
    [InlineData("module.manifest.json", "null", "invalid-manifest")]
    [InlineData("module.manifest.json", "{\"schemaVersion\":1.5}", "invalid-manifest")]
    [InlineData("module.manifest.json", "{\"schemaVersion\":2147483648}", "invalid-manifest")]
    [InlineData("module.manifest.json", "{\"schemaVersion\":1e100}", "invalid-manifest")]
    public async Task InvalidMetadataDoesNotUnloadAValidNeighborOnReload(
        string metadataEntry,
        string metadata,
        string expectedCode)
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        RuntimeModulePackageTests.CreatePackage(root, "contextfixture", "contextfixture", "v1.0.0");
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(root), log)
        {
            EnableFileWatching = false,
            ModuleDataRoot = Path.Combine(temp.Path, "ModuleData"),
        };
        host.Attach(registry, bus);
        host.Start();
        Assert.True(Assert.Single(host.Modules).Attached);

        var broken = RuntimeModulePackageTests.CreatePackage(root, "broken", "broken", "v1.0.0");
        using (var archive = ZipFile.Open(broken, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry(metadataEntry);
            Assert.NotNull(entry);
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry(metadataEntry).Open(), new UTF8Encoding(false));
            writer.Write(metadata);
        }

        Assert.False(ModuleNupkg.TryRead(broken, out _, out var code, out var error));
        Assert.Equal(expectedCode, code);
        Assert.False(string.IsNullOrWhiteSpace(error));
        var discovery = new RuntimeModuleDiscoverySource(root).Discover();
        Assert.Equal("contextfixture", Assert.Single(discovery.Modules).Name);
        Assert.Contains(discovery.Diagnostics, item => item.Path == broken && item.Code == expectedCode);

        host.Reload();

        var neighbor = Assert.Single(host.Modules);
        Assert.Equal("contextfixture", neighbor.ModuleName);
        Assert.Equal("v1.0.0", neighbor.Version);
        Assert.True(neighbor.Attached, string.Join("; ", neighbor.AttachFailures));
        Assert.True(host.IsReady);
        Assert.Contains(host.DiscoveryDiagnostics, item => item.Path == broken && item.Code == expectedCode);
        var result = await bus.InvokeAsync("contextfixture.context-probe", "Test");
        Assert.True(result.Success, result.Message);
        Assert.Equal("registered", result.Message);
    }
}
