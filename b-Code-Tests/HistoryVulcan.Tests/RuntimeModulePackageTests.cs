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
    public void InstallIsIdempotentUpgradesAndRemovesValidatedPackages()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0", includeHistory: true);
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new TestLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
            EnableUiModules = false,
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

            var idempotent = host.InstallPackage(first);
            Assert.True(idempotent.Success, idempotent.Message);
            Assert.Contains("无需替换", idempotent.Message, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var upgraded = host.InstallPackage(second);
            Assert.True(upgraded.Success, upgraded.Message);
            Assert.Equal("v2.0.0", Assert.Single(host.Modules).Version);

            var removed = host.RemovePackage("contextfixture");
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
    public void FailedUpgradeRestoresThePreviouslyLoadedPackage()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var good = CreatePackage(candidates, "good", "contextfixture", "v1.0.0");
        var broken = CreatePackage(candidates, "broken", "contextfixture", "v2.0.0", validAssembly: false);
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);

        var registry = new CommandRegistry();
        var log = new TestLog();
        var settings = new MemorySettings();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
            EnableUiModules = false,
        };

        try
        {
            host.Attach(registry, bus, settings, Path.Combine(temp.Path, "data"));
            host.Start();
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            Assert.True(host.InstallPackage(good).Success);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var failed = host.InstallPackage(broken);
            Assert.False(failed.Success);
            Assert.Contains("旧包已恢复", failed.Message, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Reload();
            Assert.Equal("v1.0.0", Assert.Single(host.Modules).Version);
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
        var log = new TestLog();
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
            EnableUiModules = false,
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
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    [Fact]
    public async Task InstallUnloadsFrontendBeforeReplacingLockedRuntimePackage()
    {
        using var temp = new TemporaryDirectory();
        var runtime = Path.Combine(temp.Path, "HistoryVulcan", "Modules");
        var candidates = Directory.CreateDirectory(Path.Combine(temp.Path, "candidates")).FullName;
        var first = CreatePackage(candidates, "first", "contextfixture", "v1.0.0");
        var second = CreatePackage(candidates, "second", "contextfixture", "v2.0.0");
        var previous = Environment.GetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable);
        var registry = new CommandRegistry();
        var log = new TestLog();
        var bus = new CommandBus(registry, log);
        using var host = new ModuleHost(new RuntimeModuleDiscoverySource(runtime), log)
        {
            EnableFileWatching = false,
            EnableUiModules = false,
        };

        try
        {
            host.Attach(registry, bus, new MemorySettings(), Path.Combine(temp.Path, "data"));
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v1.0.0");
            host.Start();
            Assert.True(host.InstallPackage(first).Success);

            var manifest = Path.Combine(runtime, "contextfixture", "module.manifest.json");
            using var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read);
            var calls = new List<string>();
            bus.FrontendExecutor = (text, _, _) =>
            {
                calls.Add(text);
                locked.Dispose();
                return Task.FromResult(CommandResult.Ok("前端已卸载"));
            };

            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, "v2.0.0");
            var result = await ServiceComposer.InstallRuntimePackageAsync(host, bus, second);

            Assert.True(result.Success, result.Message);
            Assert.Equal(["vulcan.module.unload name=contextfixture"], calls);
            Assert.Equal("v2.0.0", Assert.Single(host.Modules).Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ContextFixtureModuleInfo.VersionVariable, previous);
        }
    }

    internal static string CreatePackage(
        string parent,
        string directoryName,
        string name,
        string version,
        bool includeHistory = false,
        bool validAssembly = true)
    {
        var package = Directory.CreateDirectory(Path.Combine(parent, directoryName)).FullName;
        var artifact = Path.Combine(package, "ContextFixture.dll");
        if (validAssembly)
            File.Copy(typeof(ContextFixtureModuleInfo).Assembly.Location, artifact);
        else
            File.WriteAllText(artifact, "not a managed assembly");
        WriteManifest(package, name, version, "ContextFixture.dll");
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

    private static void WriteManifest(string package, string name, string version, string artifact)
        => File.WriteAllText(
            Path.Combine(package, "module.manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                type = "HistoryVulcan.Module",
                name,
                version,
                artifact,
                ui = false,
            }));

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => [.. _values];
    }

    private sealed class TestLog : IShellLog
    {
        public List<ShellLogEntry> Entries { get; } = [];
        public void Log(ShellLogLevel level, string category, string message)
            => Entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
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
