using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class PublicApiConsistencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-api-" + Guid.NewGuid().ToString("N"));

    public PublicApiConsistencyTests()
    {
        foreach (var project in new[] { "HistoryVulcan.Core", "HistoryVulcan.Services", "HistoryVulcan.ServiceHost" })
        {
            var directory = Path.Combine(_root, "b-Code-HistoryVulcan", project);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "PublicAPI.Shipped.txt"), "#nullable enable\nExample.Existing\n");
            File.WriteAllText(Path.Combine(directory, "PublicAPI.Unshipped.txt"),
                "#nullable enable\nExample.Added\n*REMOVED*Example.Existing\n");
        }
    }

    [Fact]
    public void DeclaredApiDoesNotRequireAVersionBaseline()
    {
        QualityGates.AssertPublicApiConsistency(_root, TextWriter.Null);
    }

    [Theory]
    [InlineData("PublicAPI.Shipped.txt")]
    [InlineData("PublicAPI.Unshipped.txt")]
    public void MissingDeclarationFilesAreRejected(string file)
    {
        File.Delete(Path.Combine(_root, "b-Code-HistoryVulcan", "HistoryVulcan.Core", file));
        var error = Assert.Throws<InvalidOperationException>(() =>
            QualityGates.AssertPublicApiConsistency(_root, TextWriter.Null));
        Assert.Contains("缺少 " + file, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovalOfAnUnshippedApiIsRejected()
    {
        File.AppendAllText(Path.Combine(_root, "b-Code-HistoryVulcan", "HistoryVulcan.Core", "PublicAPI.Unshipped.txt"),
            "*REMOVED*Example.NeverShipped\n");
        var error = Assert.Throws<InvalidOperationException>(() =>
            QualityGates.AssertPublicApiConsistency(_root, TextWriter.Null));
        Assert.Contains("Example.NeverShipped", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
