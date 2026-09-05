using System.Text.Json;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ProjectContractValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-contract-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("duplicate", "重复需求编号")]
    [InlineData("missing-test", "验收测试不存在")]
    [InlineData("missing-acceptance", "需求缺少本仓验收测试")]
    public void InvalidRequirementContractsAreRejected(string mutation, string diagnostic)
    {
        Directory.CreateDirectory(Path.Combine(_root, "b-Office", "current"));
        Directory.CreateDirectory(Path.Combine(_root, "tests"));
        File.WriteAllText(Path.Combine(_root, "tests", "FixtureTests.cs"),
            "public class FixtureTests { [Fact] public void ChecksBehavior() {} }");
        var section = "### REQ-HOST-001 Host\n\n验收：`FixtureTests.ChecksBehavior`。\n";
        var content = mutation switch
        {
            "duplicate" => section + section,
            "missing-test" => section.Replace("ChecksBehavior", "Missing", StringComparison.Ordinal),
            _ => "### REQ-HOST-001 Host\n",
        };
        File.WriteAllText(Path.Combine(_root, "b-Office", "current", "contract.md"), content);
        using var manifest = JsonDocument.Parse("""
            { "documents": { "technical": "b-Office/current/contract.md" },
              "contract": { "requirementTestRoot": "tests" } }
            """);
        var errors = new List<string>();
        ProjectContract.CheckRequirements(
            _root, manifest.RootElement, manifest.RootElement.GetProperty("contract"), errors);
        Assert.Contains(errors, error => error.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryDeclaredAcceptanceIsAnActualXunitTest()
    {
        var root = RepositoryPaths.Root();
        var content = File.ReadAllText(Path.Combine(root, "b-Office", "current", "技术合同.md"));
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
            content, @"验收：\s*`(?<type>\w+)\.(?<method>\w+)`"))
        {
            var type = typeof(ProjectContractValidationTests).Assembly.GetType("HistoryVulcan.Tests." + match.Groups["type"].Value);
            Assert.NotNull(type);
            var method = type.GetMethod(match.Groups["method"].Value);
            Assert.NotNull(method);
            Assert.Contains(method.GetCustomAttributes(inherit: true), attribute => attribute is FactAttribute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
