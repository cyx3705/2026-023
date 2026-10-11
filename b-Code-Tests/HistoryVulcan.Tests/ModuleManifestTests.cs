using System.Text.Json.Nodes;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ModuleManifestTests
{
    [Theory]
    [InlineData("schemaVersion", "2")]
    [InlineData("type", "\"Other.Module\"")]
    [InlineData("ui", "null")]
    public void InvalidManifestIsIsolatedAndValidNeighborRemains(string field, string json)
    {
        using var temp = new TemporaryDirectory();
        var valid = RuntimeModulePackageTests.CreatePackage(temp.Path, "valid", "valid", "1.0.0");
        var broken = RuntimeModulePackageTests.CreatePackage(temp.Path, "broken", "broken", "1.0.0");
        ModuleNupkg.RewriteManifest(broken, text =>
        {
            var manifest = JsonNode.Parse(text)!;
            manifest[field] = JsonNode.Parse(json);
            return manifest.ToJsonString();
        });
        var snapshot = new RuntimeModuleDiscoverySource(temp.Path).Discover();
        Assert.Equal(valid, Assert.Single(snapshot.Modules).PackagePath);
        Assert.NotEmpty(snapshot.Diagnostics);
        Assert.False(ModuleManifestReader.TryReadPackage(broken, out _, out _));
    }

    [Fact]
    public void MalformedJsonAndMissingManifestAreRejected()
    {
        using var temp = new TemporaryDirectory();
        Assert.False(ModuleManifestReader.TryReadPackage(temp.Path, out _, out _));
        var junk = Path.Combine(temp.Path, "junk.nupkg");
        File.WriteAllText(junk, "{");
        Assert.False(ModuleManifestReader.TryReadPackage(junk, out _, out _));
        Assert.False(ModuleManifestReader.TryReadPackage("relative-root", out _, out _));
    }

    [Fact]
    public void OldGatewayMetadataIsIgnoredAndLegacyDiscoveryPathsAreInactive()
    {
        using var temp = new TemporaryDirectory();
        var valid = RuntimeModulePackageTests.CreatePackage(temp.Path, "valid", "valid", "1.0.0");
        ModuleNupkg.RewriteManifest(valid, text =>
        {
            var manifest = JsonNode.Parse(text)!;
            manifest["mcpExposure"] = "invalid-retired-policy";
            return manifest.ToJsonString();
        });
        File.WriteAllText(Path.Combine(temp.Path, "Bare.dll"), "not an assembly");
        var nested = Path.Combine(temp.Path, "2026-100-HistoryLegacy");
        RuntimeModulePackageTests.CreatePackage(nested, "z-Publish", "legacy", "1.0.0");
        var snapshot = new RuntimeModuleDiscoverySource(temp.Path).Discover();
        Assert.Equal("valid", Assert.Single(snapshot.Modules).Name);
    }

    [Fact]
    public void DuplicateNamesRejectEveryCandidate()
    {
        using var temp = new TemporaryDirectory();
        RuntimeModulePackageTests.CreatePackage(temp.Path, "one", "duplicate", "1.0.0");
        RuntimeModulePackageTests.CreatePackage(temp.Path, "two", "duplicate", "1.0.0");
        var snapshot = new RuntimeModuleDiscoverySource(temp.Path).Discover();
        Assert.Empty(snapshot.Modules);
        Assert.Equal(2, snapshot.Diagnostics.Count(item => item.Code == "duplicate-name"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "HistoryVulcan.ManifestTests", Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
