using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class NuGetModuleVersionTests
{
    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("V1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("v1.0.0-rc1", "1.0.0-rc1")]
    [InlineData("V1.0.0-rc1", "1.0.0-rc1")]
    public async Task NuspecUsesNuGetVersionWhileTheLoadedModuleKeepsItsOriginalVersion(
        string moduleVersion,
        string packageVersion)
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        var package = RuntimeModulePackageTests.CreatePackage(root, "contextfixture", "contextfixture", moduleVersion);
        Assert.Equal(packageVersion, ReadNuspecVersion(package, "contextfixture"));
        Assert.True(ModuleNupkg.TryRead(package, out var entry, out _, out var error), error);
        Assert.Equal(moduleVersion, entry.Version);

        var previousVersion = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        try
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, moduleVersion);
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

            var loaded = Assert.Single(host.Modules);
            Assert.Equal(moduleVersion, loaded.Version);
            Assert.True(loaded.Attached, string.Join("; ", loaded.AttachFailures));
            var result = await bus.InvokeAsync("contextfixture.context-probe", "Test");
            Assert.True(result.Success, result.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previousVersion);
        }
    }

    [Theory]
    [InlineData("v2.3.4")]
    [InlineData("V2.3.4-rc1")]
    public void PackingAManifestWithoutVersionAddsTheOriginalModuleVersion(string moduleVersion)
    {
        using var temp = new TemporaryDirectory();
        const string name = "HistoryVersionSample";
        var package = Path.Combine(temp.Path, name + ".nupkg");
        ModuleNupkg.PackBytes(package, name, moduleVersion, new Dictionary<string, byte[]>
        {
            [ModuleNupkg.ManifestFileName] = """{"schemaVersion":1,"type":"HistoryVulcan.Module","ui":false}"""u8.ToArray(),
        });

        using (var archive = ZipFile.OpenRead(package))
        {
            using var stream = archive.GetEntry(ModuleNupkg.ManifestFileName)!.Open();
            using var document = JsonDocument.Parse(stream);
            Assert.Equal(moduleVersion, document.RootElement.GetProperty("version").GetString());
        }

        Assert.True(ModuleNupkg.TryRead(package, out var entry, out _, out var error), error);
        Assert.Equal(moduleVersion, entry.Version);
        Assert.Equal(moduleVersion[1..], ReadNuspecVersion(package, name));
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("V1.0.0")]
    public void LegacyNuspecWithAPrefixedVersionRemainsReadable(string moduleVersion)
    {
        using var temp = new TemporaryDirectory();
        var package = RuntimeModulePackageTests.CreatePackage(temp.Path, "legacy", "legacy", moduleVersion);
        RewriteNuspecVersion(package, "legacy", moduleVersion);

        Assert.Equal(moduleVersion, ReadNuspecVersion(package, "legacy"));
        Assert.True(ModuleNupkg.TryRead(package, out var entry, out _, out var error), error);
        Assert.Equal(moduleVersion, entry.Version);
    }

    [Theory]
    [InlineData("\"v2.0.0\"")]
    [InlineData("\"V2.0.0\"")]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("123")]
    public void AMismatchedOrInvalidManifestVersionIsRejected(string manifestVersionJson)
    {
        using var temp = new TemporaryDirectory();
        const string name = "HistoryVersionSample";
        var package = Path.Combine(temp.Path, name + ".nupkg");
        var manifest = $$"""{"schemaVersion":1,"type":"HistoryVulcan.Module","ui":false,"version":{{manifestVersionJson}}}""";
        ModuleNupkg.PackBytes(package, name, "v1.0.0", new Dictionary<string, byte[]>
        {
            [ModuleNupkg.ManifestFileName] = Encoding.UTF8.GetBytes(manifest),
        });

        Assert.False(ModuleNupkg.TryRead(package, out _, out var code, out var error));
        Assert.Equal("invalid-manifest", code);
        Assert.Contains("version", error, StringComparison.Ordinal);
    }

    [Fact]
    public void PackingTheSamePayloadHasStableBytesRegardlessOfInsertionOrderAndFileTimes()
    {
        using var temp = new TemporaryDirectory();
        var first = Path.Combine(temp.Path, "first.nupkg");
        var second = Path.Combine(temp.Path, "second.nupkg");
        var files = new Dictionary<string, byte[]>
        {
            ["z.txt"] = "last"u8.ToArray(),
            [ModuleNupkg.ManifestFileName] = """{"schemaVersion":1,"type":"HistoryVulcan.Module","ui":false}"""u8.ToArray(),
            ["a.txt"] = "first"u8.ToArray(),
        };
        ModuleNupkg.PackBytes(first, "HistoryStableSample", "v1.0.0", files);
        var originalHash = ModuleNupkg.Hash(first);
        File.SetLastWriteTimeUtc(first, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var reversed = files.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value);
        ModuleNupkg.PackBytes(second, "HistoryStableSample", "v1.0.0", reversed);
        ModuleNupkg.PackBytes(first, "HistoryStableSample", "v1.0.0", files);

        Assert.Equal(originalHash, ModuleNupkg.Hash(first));
        Assert.True(ModuleNupkg.SameContent(first, second));
        using var archive = ZipFile.OpenRead(second);
        Assert.All(archive.Entries, item => Assert.Equal(new DateTime(1980, 1, 1), item.LastWriteTime.DateTime));
    }

    private static string ReadNuspecVersion(string package, string name)
    {
        using var archive = ZipFile.OpenRead(package);
        using var stream = archive.GetEntry(name + ".nuspec")!.Open();
        return XDocument.Load(stream).Descendants().Single(item => item.Name.LocalName == "version").Value;
    }

    private static void RewriteNuspecVersion(string package, string name, string version)
    {
        using var archive = ZipFile.Open(package, ZipArchiveMode.Update);
        var entry = archive.GetEntry(name + ".nuspec")!;
        XDocument document;
        using (var stream = entry.Open())
            document = XDocument.Load(stream);
        document.Descendants().Single(item => item.Name.LocalName == "version").Value = version;
        entry.Delete();
        using var destination = archive.CreateEntry(name + ".nuspec").Open();
        document.Save(destination);
    }
}
