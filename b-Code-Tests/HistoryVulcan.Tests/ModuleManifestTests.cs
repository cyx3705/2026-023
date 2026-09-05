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
    [InlineData("artifact", "\"../Outside.dll\"")]
    [InlineData("artifact", "\"bad\\u0000.dll\"")]
    [InlineData("deps", "[\"Missing.dll\"]")]
    [InlineData("docs", "\"Missing.md\"")]
    public void InvalidManifestIsIsolatedAndValidNeighborRemains(string field, string json)
    {
        using var temp = new TemporaryDirectory();
        var valid = RuntimeModulePackageTests.CreatePackage(temp.Path, "valid", "valid", "1.0.0");
        var broken = RuntimeModulePackageTests.CreatePackage(temp.Path, "broken", "broken", "1.0.0");
        var path = System.IO.Path.Combine(broken, ModuleManifestReader.ManifestFileName);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest[field] = JsonNode.Parse(json);
        File.WriteAllText(path, manifest.ToJsonString());
        RuntimeModulePackageTests.WriteChecksums(broken);
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
        File.WriteAllText(System.IO.Path.Combine(temp.Path, ModuleManifestReader.ManifestFileName), "{");
        Assert.False(ModuleManifestReader.TryReadPackage(temp.Path, out _, out _));
        Assert.False(ModuleManifestReader.TryReadPackage("relative-root", out _, out _));
    }

    [Fact]
    public void OldGatewayMetadataIsIgnoredAndLegacyDiscoveryPathsAreInactive()
    {
        using var temp = new TemporaryDirectory();
        var valid = RuntimeModulePackageTests.CreatePackage(temp.Path, "valid", "valid", "1.0.0");
        var path = System.IO.Path.Combine(valid, ModuleManifestReader.ManifestFileName);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!;
        manifest["mcpExposure"] = "invalid-retired-policy";
        File.WriteAllText(path, manifest.ToJsonString());
        RuntimeModulePackageTests.WriteChecksums(valid);
        File.WriteAllText(System.IO.Path.Combine(temp.Path, "Bare.dll"), "not an assembly");
        RuntimeModulePackageTests.CreatePackage(
            System.IO.Path.Combine(temp.Path, "2026-100-HistoryLegacy"), "z-Publish", "legacy", "1.0.0");
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
