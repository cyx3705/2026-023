using System.Text.Json;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ReleasePipelineTests
{
    [Fact]
    public void ToolProcessRejectsPowerShell()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ToolProcess.Run("powershell.exe", ["-Command", "1"], Path.GetTempPath(), TextWriter.Null, "probe"));
        Assert.Contains("禁止", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CatalogLoadsHostAndModulesFromEngPipeline()
    {
        var registry = Path.Combine(RepositoryPaths.Root(), "b-Code-Eng", "pipeline", "module-publish.manifest.json");
        var targets = ReleaseCatalog.Load(registry);
        Assert.Contains(targets, target => target.Name == "HistoryVulcan" && target.Kind == "host");
        var aurora = Assert.Single(targets, target => target.Name == "HistoryAurora");
        Assert.NotNull(aurora.Package);
        Assert.Null(aurora.Package!.PublishTargetFramework);
        Assert.DoesNotContain(
            aurora.Validation,
            step => step.Tool.Contains("powershell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HostResolutionIgnoresExtensibleModuleEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "vulcan-host-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = Path.Combine(root, "module-publish.manifest.json");
            File.WriteAllText(registry, """
                {
                  "schemaVersion": 1,
                  "hosts": [{ "name": "HistoryVulcan", "freezeTag": "v5.1.2" }],
                  "modules": [{ "name": "FutureModule" }, { "kind": "not-a-module" }]
                }
                """);

            var target = ReleaseCatalog.RequireHost(registry);

            Assert.Equal("HistoryVulcan", target.Name);
            Assert.Equal("host", target.Kind);
            Assert.Equal(target.Name, ReleaseCatalog.Require(registry, "HistoryVulcan").Name);

            File.WriteAllText(registry, """
                {
                  "schemaVersion": 1,
                  "hosts": [{ "name": "HistoryVulcan", "freezeTag": "v5.1.2" }],
                  "modules": []
                }
                """);
            Assert.Equal("HistoryVulcan", ReleaseCatalog.RequireHost(registry).Name);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostPipelineSourcesDoNotNameAConcreteModule()
    {
        var root = RepositoryPaths.Root();
        var sources = new[]
        {
            Path.Combine(root, "b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "Pipeline", "ReleaseCatalog.cs"),
            Path.Combine(root, "b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "Pipeline", "ReleaseEngine.cs"),
            Path.Combine(root, "b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "Pipeline", "HostSnapshotBuilder.cs"),
        };
        var registryPath = Path.Combine(root, "b-Code-Eng", "pipeline", "module-publish.manifest.json");
        using var registry = JsonDocument.Parse(File.ReadAllText(registryPath));
        var concreteModules = registry.RootElement
            .GetProperty("modules")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString() ?? "")
            .Where(name => name.Length > 0)
            .ToArray();

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            foreach (var module in concreteModules)
                Assert.DoesNotContain(module, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PackageRegistryDoesNotDuplicateTheBuildOutputDirectory()
    {
        var registry = Path.Combine(RepositoryPaths.Root(), "b-Code-Eng", "pipeline", "module-publish.manifest.json");
        var text = File.ReadAllText(registry);
        Assert.DoesNotContain("outputDirectory", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HostProjectContractPassesOnThisRepository()
    {
        var root = RepositoryPaths.Root();
        var registry = Path.Combine(root, "b-Code-Eng", "pipeline", "module-publish.manifest.json");
        ProjectContract.Validate(root, "host", instantiation: true, registry);
    }

    [Fact]
    public void QualityGatesPassOnThisRepository()
    {
        var root = RepositoryPaths.Root();
        QualityGates.AssertSourceQuality(root, TextWriter.Null);
        var vulcan = ReleaseCatalog.Require(
            Path.Combine(root, "b-Code-Eng", "pipeline", "module-publish.manifest.json"),
            "HistoryVulcan");
        QualityGates.AssertPublicApiBaseline(root, ReleaseCatalog.ReadVersion(root, vulcan), TextWriter.Null);
    }

    [Fact]
    public void ReleaseCommandsNoLongerShellOutToPowerShell()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root(),
            "b-Code-HistoryVulcan",
            "HistoryVulcan.Services",
            "Development",
            "ReleaseCommands.cs"));
        Assert.DoesNotContain("powershell.exe", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Publish-OneHistoryModule.ps1", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PipelineEnginePowerShellScriptsAreRemoved()
    {
        var eng = Path.Combine(RepositoryPaths.Root(), "b-Code-Eng");
        string[] removed =
        [
            "Assert-PublicApiBaseline.ps1",
            "Build-HistoryVulcanPackage.ps1",
            "Test-ProjectContract.ps1",
            "Test-QualityGate.ps1",
            Path.Combine("pipeline", "Publish-OneHistoryModule.ps1"),
            Path.Combine("pipeline", "OneHistory.ContractCore.ps1"),
            Path.Combine("pipeline", "OneHistory.HostContract.ps1"),
            Path.Combine("pipeline", "OneHistory.ModuleContract.ps1"),
        ];
        foreach (var relative in removed)
            Assert.False(File.Exists(Path.Combine(eng, relative)), relative);
    }

    [Fact]
    public void SnapshotHashesRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "vulcan-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            File.WriteAllText(Path.Combine(root, "docs", "b.md"), "doc");
            SnapshotHashes.Write(root);
            SnapshotHashes.Assert(root, skipHistory: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
