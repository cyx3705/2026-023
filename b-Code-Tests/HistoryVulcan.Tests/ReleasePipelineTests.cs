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
        Assert.DoesNotContain(
            aurora.Validation,
            step => step.Tool.Contains("powershell", StringComparison.OrdinalIgnoreCase));
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
