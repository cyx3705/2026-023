using System.Security.Cryptography;
using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class OfflineModuleInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-offline-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackupAndDataCopyFailuresPreserveTheCompleteInstalledPackage(bool allowBackup)
    {
        var runtime = Path.Combine(_root, "Modules");
        var original = RuntimeModulePackageTests.CreatePackage(runtime, "Sample", "Sample", "1.0.0");
        var incoming = RuntimeModulePackageTests.CreatePackage(_root, "incoming", "Sample", "2.0.0");
        Directory.CreateDirectory(Path.Combine(original, "data", "nested"));
        var data = Path.Combine(original, "data", "nested", "state");
        File.WriteAllText(data, "original user data");
        var expected = Hashes(original);
        OfflineModuleInstall.Result result;
        using (File.Open(data, FileMode.Open, FileAccess.Read, allowBackup ? FileShare.Delete : FileShare.Read))
            result = OfflineModuleInstall.Install(runtime, incoming);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(expected, Hashes(original));
        Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(original, out var entry, out _, out var error), error);
        Assert.Equal("1.0.0", entry.Version);
    }

    [Fact]
    public void OfflineUpgradeCopiesNestedDataAndCommitsTheVerifiedPayload()
    {
        var runtime = Path.Combine(_root, "Modules");
        var original = RuntimeModulePackageTests.CreatePackage(runtime, "Sample", "Sample", "1.0.0");
        var incoming = RuntimeModulePackageTests.CreatePackage(_root, "incoming", "Sample", "2.0.0");
        Directory.CreateDirectory(Path.Combine(original, "data", "nested"));
        File.WriteAllText(Path.Combine(original, "data", "nested", "state"), "original user data");
        var result = OfflineModuleInstall.Install(runtime, incoming);
        Assert.Equal(0, result.ExitCode);
        Assert.True(RuntimeModuleDiscoverySource.TryReadPackage(original, out var entry, out _, out var error), error);
        Assert.Equal("2.0.0", entry.Version);
        Assert.Equal("original user data", File.ReadAllText(Path.Combine(original, "data", "nested", "state")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(incoming, "ContextFixture.dll")),
            File.ReadAllBytes(Path.Combine(original, "ContextFixture.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, ".module-transactions")));
    }

    private static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
