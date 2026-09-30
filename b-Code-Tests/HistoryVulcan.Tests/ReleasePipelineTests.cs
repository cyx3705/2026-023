using HistoryVulcan.Services.Development;
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
    public void HostProjectContractPassesOnThisRepository()
    {
        var root = RepositoryPaths.Root();
        ProjectContract.Validate(root, "host", instantiation: true, Path.Combine(root, ReleaseCommands.FreezeFile));
    }

    [Fact]
    public void DevelopmentManualMakesDianaThePrimaryDocumentationChannel()
    {
        var root = RepositoryPaths.Root();
        var agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));
        var manual = File.ReadAllText(Path.Combine(root, "b-Office", "package", "模块开发手册.md"));

        foreach (var contract in new[] { agents, manual })
        {
            Assert.Contains("diana.docs.catalog", contract, StringComparison.Ordinal);
            Assert.Contains("Diana MCP 工具未暴露或调用失败", contract, StringComparison.Ordinal);
            Assert.Contains("正式 `z-Publish/docs`", contract, StringComparison.Ordinal);
            Assert.Contains("diana.view.windows", contract, StringComparison.Ordinal);
            Assert.Contains("diana.view.capture", contract, StringComparison.Ordinal);
        }

        Assert.Contains("禁止为只读观察调用 Computer Use", manual, StringComparison.Ordinal);
        Assert.Contains("不移动鼠标、不切换前台", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("若本机 Cursor 已接上 Diana MCP，可以再", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("MCP 可用时再", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("`diana.docs.*` | 可选", manual, StringComparison.Ordinal);
        Assert.DoesNotContain("HistoryAurora `b-Office/current/", manual, StringComparison.Ordinal);
    }

    [Fact]
    public void QualityGatesPassOnThisRepository()
    {
        var root = RepositoryPaths.Root();
        QualityGates.AssertSourceQuality(root, TextWriter.Null);
        var vulcan = ReleaseCatalog.Host();
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
