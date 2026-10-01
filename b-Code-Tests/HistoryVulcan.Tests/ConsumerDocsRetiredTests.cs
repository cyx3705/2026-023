using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 6.1.0（DEC-072，REQ-HOST-089）：消费文档退役。发布管线不再把 b-Office/package 复制进 z 快照，
/// 说明书只来自指令注册时的自描述（由 Diana 现查 vulcan.command.list 渲染）。
/// </summary>
public sealed class ConsumerDocsRetiredTests
{
    [Fact]
    public void SnapshotBuildersNoLongerShipConsumerDocuments()
    {
        var pipeline = Path.Combine(
            RepositoryPaths.Root(), "b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "Pipeline");
        foreach (var file in new[] { "ModuleSnapshotBuilder.cs", "HostSnapshotBuilder.cs" })
        {
            var source = File.ReadAllText(Path.Combine(pipeline, file));
            Assert.DoesNotContain("\"docs\"", source, StringComparison.Ordinal);
            Assert.DoesNotContain("consumer-docs.json", source, StringComparison.Ordinal);
            Assert.DoesNotContain("b-Office/package", source, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            typeof(ReleaseTarget).GetProperties(),
            property => property.Name.Contains("Document", StringComparison.Ordinal));
    }

    [Fact]
    public void HostRepositoryHasNoConsumerDocumentSource()
    {
        var root = RepositoryPaths.Root();
        Assert.False(Directory.Exists(Path.Combine(root, "b-Office", "package")), "b-Office/package 已退役");
        Assert.False(File.Exists(Path.Combine(root, "b-Code-Eng", "release", "consumer-docs.json")), "consumer-docs.json 已退役");
        Assert.True(File.Exists(Path.Combine(root, "b-Office", "current", "模块开发手册.md")), "模块开发手册是现行文档");
    }

    [Fact]
    public void LegacyPackageDocumentsKeyIsIgnored()
    {
        var root = Path.Combine(Path.GetTempPath(), "vulcan-legacy-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, ReleaseCatalog.ProjectManifestFile), """
                {
                  "schemaVersion": 1,
                  "project": { "name": "HistorySample" },
                  "publish": {
                    "versionProps": "b-Code\\Version.props",
                    "versionProperty": "HistorySampleVersion",
                    "sourceManifest": "b-Code\\module.manifest.json",
                    "packageDocuments": "b-Office\\package",
                    "package": { "project": "b-Code\\HistorySample.csproj", "files": ["HistorySample.dll"] }
                  }
                }
                """);

            var target = ReleaseCatalog.TryLoadModule(root, "2026-099-HistorySample");

            Assert.NotNull(target);
            Assert.Equal("HistorySample", target!.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
