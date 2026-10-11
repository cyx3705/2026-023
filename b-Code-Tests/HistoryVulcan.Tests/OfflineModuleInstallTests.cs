using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class OfflineModuleInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-offline-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupFailurePreservesTheCompleteInstalledPackage(bool allowDelete)
    {
        var runtime = Path.Combine(_root, "Modules");
        var original = RuntimeModulePackageTests.CreatePackage(runtime, "Sample", "Sample", "1.0.0");
        var incoming = RuntimeModulePackageTests.CreatePackage(_root, "incoming", "Sample", "2.0.0");
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)));
        OfflineModuleInstall.Result result;
        using (File.Open(original, FileMode.Open, FileAccess.Read, allowDelete ? FileShare.Read | FileShare.Delete : FileShare.Read))
            result = OfflineModuleInstall.Install(runtime, incoming);
        if (!allowDelete)
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(expected, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original))));
        }
        else
        {
            Assert.True(result.ExitCode is 0 or 1, result.Message);
        }

        Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(original, out var entry, out _, out var error), error);
        Assert.Equal(result.ExitCode == 0 ? "2.0.0" : "1.0.0", entry.Version);
    }

    [Fact]
    public void OfflineUpgradeCommitsTheVerifiedPayload()
    {
        var runtime = Path.Combine(_root, "Modules");
        var original = RuntimeModulePackageTests.CreatePackage(runtime, "Sample", "Sample", "1.0.0");
        var incoming = RuntimeModulePackageTests.CreatePackage(_root, "incoming", "Sample", "2.0.0");
        var result = OfflineModuleInstall.Install(runtime, incoming);
        Assert.True(result.ExitCode == 0, result.Message);
        Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(original, out var entry, out _, out var error), error);
        Assert.Equal("2.0.0", entry.Version);
        Assert.Equal(File.ReadAllBytes(incoming), File.ReadAllBytes(original));
        Assert.False(Directory.Exists(Path.Combine(_root, ".module-transactions")));
    }

    [Fact]
    public void TargetOwnedByAnotherModuleIsRejectedWithoutChangingItsBytes()
    {
        var runtime = Path.Combine(_root, "Modules");
        var original = RuntimeModulePackageTests.CreatePackage(runtime, "Foo", "Bar", "1.0.0");
        var incoming = RuntimeModulePackageTests.CreatePackage(_root, "incoming", "Foo", "1.0.0");
        var expected = File.ReadAllBytes(original);

        var result = OfflineModuleInstall.Install(runtime, incoming);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Bar", result.Message, StringComparison.Ordinal);
        Assert.Contains("拒绝", result.Message, StringComparison.Ordinal);
        Assert.Equal(expected, File.ReadAllBytes(original));
        Assert.False(Directory.Exists(Path.Combine(_root, ".module-transactions")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
