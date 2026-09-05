using System.Security.Cryptography;
using HistoryVulcan.Services.Development.Pipeline;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class PublishTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-publish-test-" + Guid.NewGuid().ToString("N"));

    private (string Publish, string Incoming, string Target) Create()
    {
        var publish = Directory.CreateDirectory(Path.Combine(_root, "publish")).FullName;
        Directory.CreateDirectory(Path.Combine(publish, "old"));
        File.WriteAllText(Path.Combine(publish, "old", "payload"), "original payload");
        File.WriteAllText(Path.Combine(publish, "old", "metadata"), "original metadata");
        var incoming = Path.Combine(_root, "incoming");
        File.WriteAllText(incoming, "new payload");
        return (publish, incoming, Path.Combine(publish, "new"));
    }

    private static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .ToArray();

    [Fact]
    public void BackupFailureLeavesTheEntireOriginalPublication()
    {
        var (publish, incoming, target) = Create();
        var expected = Hashes(publish);
        using var locked = File.Open(Path.Combine(publish, "old", "payload"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => PublishTransaction.Run(
            publish, Directory.GetFileSystemEntries(publish), [(incoming, target)], TextWriter.Null));
        Assert.Equal(expected, Hashes(publish));
        Assert.Empty(Directory.GetDirectories(_root, ".publish-backup-*"));
    }

    [Fact]
    public void PartialIncomingFailureRestoresEveryOriginalFile()
    {
        var (publish, incoming, target) = Create();
        var expected = Hashes(publish);
        Assert.Throws<IOException>(() => PublishTransaction.Run(
            publish, Directory.GetFileSystemEntries(publish), [(incoming, target)], TextWriter.Null,
            move: (source, destination) =>
            {
                if (source == incoming)
                {
                    File.WriteAllText(destination, "partial copy");
                    throw new IOException("injected copy failure");
                }
                PublishTransaction.Move(source, destination);
            }));
        Assert.Equal(expected, Hashes(publish));
        Assert.Empty(Directory.GetDirectories(_root, ".publish-backup-*"));
    }

    [Fact]
    public void FailedRecoveryRetainsAnIntactBackupAndReportsItsPath()
    {
        var (publish, incoming, target) = Create();
        var expected = Hashes(publish);
        FileStream? locked = null;
        try
        {
            var error = Assert.Throws<IOException>(() => PublishTransaction.Run(
                publish, Directory.GetFileSystemEntries(publish), [(incoming, target)], TextWriter.Null,
                move: (source, destination) =>
                {
                    PublishTransaction.Move(source, destination);
                    if (source == incoming)
                    {
                        locked = File.Open(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
                        throw new IOException("injected installation failure");
                    }
                }));
            var backup = Assert.Single(Directory.GetDirectories(_root, ".publish-backup-*"));
            Assert.Contains(backup, error.Message, StringComparison.Ordinal);
            Assert.Equal(expected, Hashes(backup));
            Assert.IsType<AggregateException>(error.InnerException);
        }
        finally { locked?.Dispose(); }
    }

    [Fact]
    public void CleanupFailureReportsResidueWithoutRollingBackCommittedFiles()
    {
        var (publish, incoming, target) = Create();
        using var log = new StringWriter();
        FileStream? locked = null;
        try
        {
            PublishTransaction.Run(
                publish, Directory.GetFileSystemEntries(publish), [(incoming, target)], log,
                delete: path =>
                {
                    locked = File.Open(Path.Combine(path, "old", "payload"), FileMode.Open, FileAccess.Read, FileShare.Read);
                    PublishTransaction.Delete(path);
                });
            var backup = Assert.Single(Directory.GetDirectories(_root, ".publish-backup-*"));
            Assert.Contains(backup, log.ToString(), StringComparison.Ordinal);
            Assert.Equal("new payload", File.ReadAllText(target));
            Assert.False(Directory.Exists(Path.Combine(publish, "old")));
        }
        finally { locked?.Dispose(); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
