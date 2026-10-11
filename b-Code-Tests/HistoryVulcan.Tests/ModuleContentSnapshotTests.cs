using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(RuntimeModulePackageCollection.Name)]
public sealed class ModuleContentSnapshotTests
{
    private const string ModuleName = "contextfixture";

    [Fact]
    public void SamePackageReusesCompleteContentWhileAResourceIsLocked()
    {
        using var temp = new TemporaryDirectory();
        var package = CreatePackage(Path.Combine(temp.Path, "Modules"), "same", "v1.0.0",
            new Dictionary<string, byte[]> { ["assets/asset.txt"] = "complete asset"u8.ToArray() });
        var first = ModuleNupkg.ExtractContent(package, ModuleName);
        using var locked = File.Open(Path.Combine(first, "assets", "asset.txt"),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        var reused = ModuleNupkg.ExtractContent(package, ModuleName);

        Assert.Equal(first, reused);
        Assert.Equal("complete asset", File.ReadAllText(Path.Combine(reused, "assets", "asset.txt")));
        Assert.True(File.Exists(Path.Combine(reused, "module.manifest.json")));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(reused)!));
    }

    [Fact]
    public async Task OfflineHostReusesTheLiveHostsLockedContentWithoutChangingItsCommands()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        CreatePackage(runtime, ModuleName, "v1.0.0",
            new Dictionary<string, byte[]> { ["asset.txt"] = "live resource"u8.ToArray() }, includeFixture: true);
        var variables = new Dictionary<string, string?>
        {
            [ContextFixtureModuleInfo.NameVariable] = ModuleName,
            [ContextFixtureModuleInfo.VersionVariable] = "v1.0.0",
            [ContextFixtureModuleInfo.EnabledVariable] = null,
            [ContextFixtureModuleInfo.ConstructionVariable] = null,
            [ContextAwareFixture.FailureVariable] = null,
            [ContextAwareFixture.DataVariable] = null,
        };
        var previous = variables.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var serviceRegistry = new CommandRegistry();
        var serviceBus = new CommandBus(serviceRegistry, new NullLog());
        var offlineRegistry = new CommandRegistry();
        var offlineBus = new CommandBus(offlineRegistry, new NullLog());
        using var service = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), new NullLog())
        {
            EnableFileWatching = false,
            ModuleDataRoot = Path.Combine(temp.Path, "ModuleData"),
            RunMode = HostRunMode.Service,
        };
        using var offline = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), new NullLog())
        {
            EnableFileWatching = false,
            EnableUiModules = false,
            ModuleDataRoot = Path.Combine(temp.Path, "ModuleData"),
            RunMode = HostRunMode.OfflineCli,
        };
        try
        {
            foreach (var (key, value) in variables)
                Environment.SetEnvironmentVariable(key, value);
            service.Attach(serviceRegistry, serviceBus);
            service.Start();
            var liveModule = Assert.Single(service.Modules);
            Assert.True(liveModule.Attached, string.Join("; ", liveModule.AttachFailures));
            var serviceEnvironment = await serviceBus.ExecuteAsync("contextfixture.context-env", "test");
            Assert.True(serviceEnvironment.Success, serviceEnvironment.Message);
            var liveParts = serviceEnvironment.Message.Split('|');
            Assert.Equal(nameof(HostRunMode.Service), liveParts[2]);
            var liveContent = liveParts[4];
            using var locked = File.Open(Path.Combine(liveContent, "asset.txt"),
                FileMode.Open, FileAccess.Read, FileShare.Read);

            offline.Attach(offlineRegistry, offlineBus);
            offline.Start();
            var offlineModule = Assert.Single(offline.Modules);
            Assert.True(offlineModule.Attached, string.Join("; ", offlineModule.AttachFailures));
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, offlineModule.CommandCount);
            var offlineEnvironment = await offlineBus.ExecuteAsync("contextfixture.context-env", "test");
            Assert.True(offlineEnvironment.Success, offlineEnvironment.Message);
            var offlineParts = offlineEnvironment.Message.Split('|');
            Assert.Equal(nameof(HostRunMode.OfflineCli), offlineParts[2]);
            Assert.Equal(liveContent, offlineParts[4]);
            Assert.Equal("live resource", File.ReadAllText(Path.Combine(liveContent, "asset.txt")));
            offline.Dispose();

            var retained = await serviceBus.ExecuteAsync("contextfixture.context-probe", "test");
            Assert.True(retained.Success, retained.Message);
            Assert.Equal("registered", retained.Message);
            Assert.Equal(liveModule.InstanceId, Assert.Single(service.Modules).InstanceId);
            Assert.Equal(ContextFixtureModuleInfo.CommandCount, serviceRegistry.All().Count);
        }
        finally
        {
            offline.Dispose();
            service.Dispose();
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public void UpgradeAndRollbackKeepTheLockedOldSnapshotSeparateFromNewContent()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "Modules");
        var first = CreatePackage(runtime, "first", "v1.0.0", new Dictionary<string, byte[]>
        {
            ["current.txt"] = "old"u8.ToArray(),
            ["obsolete.txt"] = "removed in v2"u8.ToArray(),
        });
        var second = CreatePackage(runtime, "second", "v2.0.0",
            new Dictionary<string, byte[]> { ["current.txt"] = "new"u8.ToArray() });
        var oldContent = ModuleNupkg.ExtractContent(first, ModuleName);
        using var locked = File.Open(Path.Combine(oldContent, "obsolete.txt"),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        var newContent = ModuleNupkg.ExtractContent(second, ModuleName);

        Assert.NotEqual(oldContent, newContent);
        Assert.Equal("new", File.ReadAllText(Path.Combine(newContent, "current.txt")));
        Assert.False(File.Exists(Path.Combine(newContent, "obsolete.txt")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(oldContent, "current.txt")));
        Assert.Equal("removed in v2", File.ReadAllText(Path.Combine(oldContent, "obsolete.txt")));
        Assert.Equal(oldContent, ModuleNupkg.ExtractContent(first, ModuleName));
        Assert.Equal(2, Directory.GetDirectories(Path.GetDirectoryName(oldContent)!).Length);
    }

    [Fact]
    public async Task ConcurrentExtractionPublishesOneCompleteSnapshotAndCleansStaging()
    {
        using var temp = new TemporaryDirectory();
        var payload = Enumerable.Range(0, 65536).Select(index => (byte)(index % 251)).ToArray();
        var package = CreatePackage(Path.Combine(temp.Path, "Modules"), "parallel", "v1.0.0",
            new Dictionary<string, byte[]>
            {
                ["assets/payload.bin"] = payload,
                ["asset.txt"] = "complete"u8.ToArray(),
            });

        var snapshots = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            Task.Run(() => ModuleNupkg.ExtractContent(package, ModuleName))));

        Assert.All(snapshots, content =>
        {
            Assert.Equal(snapshots[0], content);
            Assert.Equal(payload, File.ReadAllBytes(Path.Combine(content, "assets", "payload.bin")));
            Assert.Equal("complete", File.ReadAllText(Path.Combine(content, "asset.txt")));
            Assert.True(File.Exists(Path.Combine(content, "module.manifest.json")));
        });
        Assert.Equal(snapshots[0], Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(snapshots[0])!)));
    }

    [Fact]
    public void EscapingZipEntryPublishesNoPartialSnapshotAndValidPackageCanStillExpand()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Directory.CreateDirectory(Path.Combine(temp.Path, "Modules")).FullName;
        var broken = Path.Combine(runtime, "broken.nupkg");
        using (var archive = ZipFile.Open(broken, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "safe.txt", "partial content");
            WriteEntry(archive, "../escaped.txt", "outside staging");
        }
        var brokenContent = ModuleNupkg.ContentDirectory(broken, ModuleName);

        Assert.Throws<InvalidDataException>(() => ModuleNupkg.ExtractContent(broken, ModuleName));

        Assert.False(Directory.Exists(brokenContent));
        var contentRoot = Path.GetDirectoryName(brokenContent)!;
        Assert.Empty(Directory.GetFileSystemEntries(contentRoot));
        var valid = CreatePackage(runtime, "valid", "v1.0.0",
            new Dictionary<string, byte[]> { ["safe.txt"] = "complete valid content"u8.ToArray() });
        var validContent = ModuleNupkg.ExtractContent(valid, ModuleName);
        Assert.Equal("complete valid content", File.ReadAllText(Path.Combine(validContent, "safe.txt")));
        Assert.True(File.Exists(Path.Combine(validContent, "module.manifest.json")));
        Assert.Equal(validContent, Assert.Single(Directory.GetDirectories(contentRoot)));
    }

    private static string CreatePackage(
        string runtime,
        string fileName,
        string version,
        IReadOnlyDictionary<string, byte[]> assets,
        bool includeFixture = false)
    {
        var files = new Dictionary<string, byte[]>(assets, StringComparer.OrdinalIgnoreCase)
        {
            ["module.manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                type = "HistoryVulcan.Module",
                name = ModuleName,
                version,
                ui = false,
            }),
        };
        if (includeFixture)
            files["ContextFixture.dll"] = File.ReadAllBytes(typeof(ContextFixtureModuleInfo).Assembly.Location);
        var package = Path.Combine(runtime, fileName + ".nupkg");
        ModuleNupkg.PackBytes(package, ModuleName, version, files);
        return package;
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
