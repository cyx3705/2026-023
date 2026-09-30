using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Commands;
using HistoryVulcan.Services.Development;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 5.8.0（DEC-069）：宿主不登记模块（REQ-HOST-078），开发管线回执只给必要信息（REQ-HOST-079）。
/// </summary>
public sealed class ModuleSelfDescriptionTests
{
    [Fact]
    public void ModuleDescriptorComesFromTheModulesOwnProjectManifest()
    {
        var root = TempProject("""
            {
              "schemaVersion": 1,
              "project": { "name": "HistorySample" },
              "publish": {
                "versionProps": "b-Code\\Version.props",
                "versionProperty": "HistorySampleVersion",
                "sourceManifest": "b-Code\\module.manifest.json",
                "packageDocuments": "b-Office-Sample\\package",
                "package": { "project": "b-Code\\HistorySample.csproj", "files": ["HistorySample.dll"] },
                "validation": [{ "tool": "dotnet.exe", "configurations": ["Debug", "Release"], "arguments": ["test"], "description": "tests" }]
              }
            }
            """);
        try
        {
            var target = ReleaseCatalog.TryLoadModule(root, "2026-099-HistorySample");

            Assert.NotNull(target);
            Assert.Equal("HistorySample", target!.Name);
            Assert.Equal("module", target.Kind);
            Assert.Equal("2026-099-HistorySample", target.ProjectDirectory);
            Assert.Equal("b-Office-Sample\\package", target.PackageDocuments);
            // 省略的字段取默认值：模块只写和别人不一样的部分。
            Assert.Equal("module.manifest.json", target.SnapshotManifest);
            Assert.Equal("name", target.IdentityProperty);
            Assert.Equal("z-Publish", target.CandidateDirectory);
            Assert.Equal("z-Publish", target.FormalDirectory);
            Assert.Equal(["Debug", "Release"], Assert.Single(target.Validation).Configurations);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectWithoutPublishSectionIsNotAModuleAndABrokenOneSaysWhatIsMissing()
    {
        var plain = TempProject("""{ "schemaVersion": 1, "project": { "name": "Notes" } }""");
        var broken = TempProject("""{ "schemaVersion": 1, "project": { "name": "HistoryBroken" }, "publish": { "versionProperty": "V" } }""");
        var host = TempProject("""{ "schemaVersion": 1, "project": { "name": "HistoryVulcan" }, "publish": {} }""");
        try
        {
            Assert.Null(ReleaseCatalog.TryLoadModule(plain, "x"));
            var error = Assert.Throws<InvalidOperationException>(() => ReleaseCatalog.TryLoadModule(broken, "x"));
            Assert.Contains("versionProps", error.Message, StringComparison.Ordinal);
            Assert.Contains("package.project", error.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => ReleaseCatalog.TryLoadModule(host, "x"));
        }
        finally
        {
            foreach (var path in new[] { plain, broken, host })
                Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void DiscoveryScansTheLibraryWithoutAHostRegistry()
    {
        var library = Path.Combine(Path.GetTempPath(), "clio-" + Guid.NewGuid().ToString("N"));
        try
        {
            Write(library, "2026-001-Notes", """{ "project": { "name": "Notes" } }""");
            Write(library, "2026-002-HistoryA", Descriptor("HistoryA"));
            Write(library, "2026-003-HistoryB", """{ "project": { "name": "HistoryB" }, "publish": {} }""");
            Write(library, "2026-023-HistoryVulcan", Descriptor("HistoryVulcan"));
            Write(library, "0000-002-ModuleReady", Descriptor("ModuleReady").Replace(
                "\"project\":", "\"template\": { \"isTemplate\": true }, \"project\":", StringComparison.Ordinal));

            var errors = new List<string>();
            var found = ReleaseCatalog.Discover(library, errors);

            Assert.Equal("HistoryA", Assert.Single(found).Name);
            Assert.Contains(errors, error => error.StartsWith("2026-003-HistoryB:", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(library))
                Directory.Delete(library, recursive: true);
        }
    }

    [Fact]
    public void HostRepositoryHoldsNoModuleRegistry()
    {
        var root = RepositoryPaths.Root();
        Assert.False(File.Exists(Path.Combine(root, "b-Code-Eng", "pipeline", "module-publish.manifest.json")));

        // 冻结标签表只守宿主自己：除了 hosts 不许再长出别的清单。
        using var freeze = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ReleaseCommands.FreezeFile)));
        var keys = freeze.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);
        Assert.Equal(["_comment", "hosts", "schemaVersion"], keys);
        var host = Assert.Single(freeze.RootElement.GetProperty("hosts").EnumerateArray());
        Assert.Equal(ReleaseCatalog.HostName, host.GetProperty("name").GetString());

        // 管线源码里不出现任何具体模块名（History 开头、不是宿主自己的标识符）。
        var pipeline = Path.Combine(root, "b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "Pipeline");
        var concrete = new Regex(@"\bHistory(?!Vulcan)[A-Z][A-Za-z]+\b");
        foreach (var source in Directory.GetFiles(pipeline, "*.cs"))
            Assert.DoesNotMatch(concrete, File.ReadAllText(source));
    }

    [Fact]
    public void FileSummaryReportsCountAndFirstPathsOnly()
    {
        Assert.Equal("2 个文件（README.md、b/c.cs）", ReleaseCommands.FileSummary(" M README.md\n?? b/c.cs"));
        var many = string.Join("\n", Enumerable.Range(1, 30).Select(i => $" M f{i}.cs"));
        var summary = ReleaseCommands.FileSummary(many);
        Assert.StartsWith("30 个文件（f1.cs、f2.cs、f3.cs、f4.cs、f5.cs 等）", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("f6.cs", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureReceiptCarriesTheReasonButNotTheWholeLog()
    {
        var reason = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"  - 第 {i} 项"));
        var brief = ReleaseCommands.Brief(reason, 12);
        Assert.Contains("第 12 项", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("第 13 项", brief, StringComparison.Ordinal);
        Assert.EndsWith("另有 28 行，见日志", brief, StringComparison.Ordinal);
        Assert.Equal("一行", ReleaseCommands.Brief("一行\r\n", 12));
    }

    [Fact]
    public async Task CommandShowListsParametersInTheMessage()
    {
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, new NullLog());
        CommandCatalogCommands.RegisterCore(registry);
        registry.Register(new CommandDescriptor
        {
            Name = "probe.thing.run",
            Summary = "probe",
            Parameters =
            [
                new ParameterSpec { Name = "mode", Description = "模式", Required = true, AllowedValues = ["a", "b"] },
                new ParameterSpec { Name = "dry", Description = "只预检", Type = ParamType.Bool, Default = "false", AllowedValues = ["true", "false"] },
            ],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });

        var result = await bus.ExecuteAsync("vulcan.command.show probe.thing.run", "Test");

        Assert.True(result.Success, result.Message);
        Assert.Contains("\n  mode (必填 取值=a|b): 模式", result.Message, StringComparison.Ordinal);
        Assert.Contains("\n  dry (可省略 默认=false): 只预检", result.Message, StringComparison.Ordinal);
    }

    private static string Descriptor(string name) => $$"""
        {
          "project": { "name": "{{name}}" },
          "publish": {
            "versionProps": "V.props", "versionProperty": "V", "sourceManifest": "m.json",
            "package": { "project": "M.csproj", "files": ["M.dll"] }
          }
        }
        """;

    private static void Write(string library, string project, string manifest)
    {
        var directory = Path.Combine(library, project);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ReleaseCatalog.ProjectManifestFile), manifest);
    }

    private static string TempProject(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "vulcan-self-desc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ReleaseCatalog.ProjectManifestFile), manifest);
        return root;
    }
}
