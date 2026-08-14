using System.Text.Json;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ZModuleDiscoveryTests
{
    [Fact]
    public void DiscoversOnlyExplicitTypedManifestsAndKeepsPathsInsideZPackage()
    {
        using var temp = new TemporaryDirectory();
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "2026-100-HistoryFixture")).FullName;
        var valid = Directory.CreateDirectory(Path.Combine(project, "z-Valid")).FullName;
        File.WriteAllText(Path.Combine(valid, "Fixture.dll"), "fixture");
        WriteManifest(valid, "Fixture", "1.0.0", "Fixture.dll");

        var wrongType = Directory.CreateDirectory(Path.Combine(project, "z-WrongType")).FullName;
        File.WriteAllText(Path.Combine(wrongType, "Wrong.dll"), "fixture");
        WriteManifest(wrongType, "Wrong", "1.0.0", "Wrong.dll", type: "Other.Module");

        var noManifest = Directory.CreateDirectory(Path.Combine(project, "z-NoManifest")).FullName;
        File.WriteAllText(Path.Combine(noManifest, "Ignored.dll"), "fixture");

        var escaped = Directory.CreateDirectory(Path.Combine(project, "z-Escaped")).FullName;
        File.WriteAllText(Path.Combine(project, "Outside.dll"), "fixture");
        WriteManifest(escaped, "Escaped", "1.0.0", "..\\Outside.dll");

        var snapshot = new ZModuleDiscoverySource([temp.Path]).Discover();

        var module = Assert.Single(snapshot.Modules);
        Assert.Equal("Fixture", module.Name);
        Assert.Equal(Path.Combine(valid, "Fixture.dll"), module.ArtifactPath);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-type");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-artifact");
        Assert.DoesNotContain(snapshot.Modules, item => item.PackagePath == noManifest);
    }

    [Fact]
    public void OnlyProjectsMatchingTheNamingConventionParticipateInDiscovery()
    {
        // 发现根通常就是整个项目库，里面绝大多数编号项目与本体系无关（课程设计、
        // 实验、自带工具链的项目等）。它们不该被当作模块来源：3.5.0 的一段无界扫描
        // 曾把某个项目自带 JDK 的原生 DLL 喂给 Assembly.LoadFrom，直接崩掉后台服务。
        // 用命名模式而不是白名单收窄——新模块建目录即纳入，不需要有人回来改配置。
        using var temp = new TemporaryDirectory();
        CreateModule(temp.Path, "2026-200-HistoryInScope", "z-InScope", "InScope");
        CreateModule(temp.Path, "2026-201-课程设计", "z-OutOfScope", "OutOfScope");
        CreateModule(temp.Path, "tools", "z-Toolchain", "Toolchain");

        var snapshot = new ZModuleDiscoverySource([temp.Path]).Discover();

        var module = Assert.Single(snapshot.Modules);
        Assert.Equal("InScope", module.Name);
    }

    [Fact]
    public void DuplicateNamesAcrossRootsRejectEveryCandidate()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        CreateModule(first.Path, "2026-101-HistoryA", "z-One", "Duplicate");
        CreateModule(second.Path, "2026-102-HistoryB", "z-Two", "Duplicate");

        var snapshot = new ZModuleDiscoverySource([first.Path, second.Path]).Discover();

        Assert.Empty(snapshot.Modules);
        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "duplicate-name"));
    }

    [Fact]
    public void AutomaticRootWalksUpToNumberedProjectLibrary()
    {
        using var temp = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(temp.Path, "2026-001-HistoryA"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "2026-002-HistoryB"));
        Directory.CreateDirectory(Path.Combine(temp.Path, "HistoryVesta.git"));
        var nested = Directory.CreateDirectory(
            Path.Combine(temp.Path, "2026-023-HistoryVulcan", "b-Code", "host"));

        Assert.Equal(temp.Path, ZModuleDiscoverySource.FindAutomaticRoot(nested.FullName));
    }

    [Fact]
    public void AutomaticRootIgnoresBareRepoSentinelAndSingleProjectFolder()
    {
        using var sentinelOnly = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(sentinelOnly.Path, "HistoryVesta.git"));
        var nestedSentinel = Directory.CreateDirectory(Path.Combine(sentinelOnly.Path, "host"));
        Assert.Null(ZModuleDiscoverySource.FindAutomaticRoot(nestedSentinel.FullName));

        using var oneProject = new TemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(oneProject.Path, "2026-023-HistoryVulcan"));
        var nestedOne = Directory.CreateDirectory(Path.Combine(oneProject.Path, "host"));
        Assert.Null(ZModuleDiscoverySource.FindAutomaticRoot(nestedOne.FullName));
    }

    [Fact]
    public void CoerceConfiguredRootRewritesRetiredVestaLibraryToClio()
    {
        if (!Directory.Exists(ZModuleDiscoverySource.DefaultLibraryRoot))
            return;

        Assert.Equal(
            ZModuleDiscoverySource.DefaultLibraryRoot,
            ZModuleDiscoverySource.CoerceConfiguredRoot(ZModuleDiscoverySource.LegacyVestaLibrary));
        Assert.Equal(
            ZModuleDiscoverySource.DefaultLibraryRoot,
            ZModuleDiscoverySource.FindAutomaticRoot(ZModuleDiscoverySource.DefaultLibraryRoot));
    }

    [Fact]
    public void RejectsRelativeDiscoveryRoots()
    {
        Assert.Throws<ArgumentException>(() => new ZModuleDiscoverySource(["relative-root"]));
    }

    [Fact]
    public void ReportsMalformedMissingAndIncompleteArtifactsWithoutStoppingOtherRoots()
    {
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        var project = Directory.CreateDirectory(Path.Combine(first.Path, "2026-103-HistoryBroken")).FullName;

        var malformed = Directory.CreateDirectory(Path.Combine(project, "z-Malformed")).FullName;
        File.WriteAllText(Path.Combine(malformed, ZModuleDiscoverySource.ManifestFileName), "{");

        var missingUi = Directory.CreateDirectory(Path.Combine(project, "z-MissingUi")).FullName;
        File.WriteAllText(Path.Combine(missingUi, "MissingUi.dll"), "fixture");
        File.WriteAllText(
            Path.Combine(missingUi, ZModuleDiscoverySource.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                type = ZModuleDiscoverySource.ManifestType,
                name = "MissingUi",
                version = "1.0.0",
                artifact = "MissingUi.dll",
            }));

        var missingDependency = Directory.CreateDirectory(Path.Combine(project, "z-MissingDependency")).FullName;
        File.WriteAllText(Path.Combine(missingDependency, "Module.dll"), "fixture");
        File.WriteAllText(
            Path.Combine(missingDependency, ZModuleDiscoverySource.ManifestFileName),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                type = ZModuleDiscoverySource.ManifestType,
                name = "MissingDependency",
                version = "1.0.0",
                artifact = "Module.dll",
                ui = false,
                deps = new[] { "Missing.dll" },
            }));

        CreateModule(second.Path, "2026-104-HistoryValid", "z-Valid", "Valid");
        var snapshot = new ZModuleDiscoverySource([first.Path, second.Path]).Discover();

        Assert.Equal("Valid", Assert.Single(snapshot.Modules).Name);
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-json");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "missing-field");
        Assert.Contains(snapshot.Diagnostics, item => item.Code == "invalid-dependency");
        Assert.Equal(2, snapshot.Roots.Count);
    }

    private static void CreateModule(string root, string projectName, string packageName, string name)
    {
        var package = Directory.CreateDirectory(Path.Combine(root, projectName, packageName)).FullName;
        File.WriteAllText(Path.Combine(package, name + ".dll"), "fixture");
        WriteManifest(package, name, "1.0.0", name + ".dll");
    }

    private static void WriteManifest(
        string package,
        string name,
        string version,
        string artifact,
        string type = ZModuleDiscoverySource.ManifestType)
    {
        var manifest = new
        {
            schemaVersion = 1,
            type,
            name,
            version,
            artifact,
            ui = false,
        };
        File.WriteAllText(
            Path.Combine(package, ZModuleDiscoverySource.ManifestFileName),
            JsonSerializer.Serialize(manifest));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "HistoryVulcan.DiscoveryTests",
                Guid.NewGuid().ToString("N"));
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
