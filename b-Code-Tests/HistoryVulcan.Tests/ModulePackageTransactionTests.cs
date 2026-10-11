using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ModulePackageTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-transaction-" + Guid.NewGuid().ToString("N"));

    private ModulePackageTransaction Create()
    {
        var runtime = Path.Combine(_root, "Modules");
        Directory.CreateDirectory(runtime);
        var target = Path.Combine(runtime, "Sample.nupkg");
        File.WriteAllBytes(target, "old payload"u8.ToArray());
        return new ModulePackageTransaction(runtime, target);
    }

    private static void Stage(ModulePackageTransaction transaction)
        => File.WriteAllBytes(transaction.Staging, "new payload"u8.ToArray());

    private static void AssertOriginal(ModulePackageTransaction transaction)
        => Assert.Equal("old payload", File.ReadAllText(transaction.Target));

    [Fact]
    public void StagingAndBackupFailuresLeaveTheEntireOriginalUntouched()
    {
        var transaction = Create();
        File.WriteAllBytes(transaction.Staging, "incomplete"u8.ToArray());
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);

        transaction = new ModulePackageTransaction(Path.GetDirectoryName(transaction.Target)!, transaction.Target);
        using var locked = File.Open(transaction.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(transaction.BackupTarget);
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
    }

    [Fact]
    public void FailedNewPackageRestoresTheOriginalEvenAfterTheNewInstanceWritesIntoTheSlot()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(transaction.Target)!, "written-by-new-instance.txt"), "stray");
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
        Assert.False(Directory.Exists(transaction.Root));
    }

    [Fact]
    public void InstallReplacesTheWholeSlotAndCarriesNothingForward()
    {
        var transaction = Create();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(transaction.Target)!, "data-left-by-old-version.txt"), "old");
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        transaction.Commit();
        Assert.Equal("new payload", File.ReadAllText(transaction.Target));
        Assert.Equal("old", File.ReadAllText(Path.Combine(Path.GetDirectoryName(transaction.Target)!, "data-left-by-old-version.txt")));
    }

    [Fact]
    public void RollbackFailureRetainsTheBackupAndReportsItsPath()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        using var locked = File.Open(transaction.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rollback = transaction.Rollback();
        Assert.False(rollback.Success);
        Assert.Contains(transaction.Backup, rollback.Message, StringComparison.Ordinal);
        Assert.Equal("old payload", File.ReadAllText(transaction.Backup));
        Assert.Equal(ModulePackageTransaction.Stage.RecoveryRequired, transaction.CurrentStage);
    }

    [Fact]
    public void CleanupFailureDoesNotUndoACommittedInstallation()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        using var locked = File.Open(transaction.Backup, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Contains(transaction.Root, transaction.Commit(), StringComparison.Ordinal);
        Assert.Equal(ModulePackageTransaction.Stage.Committed, transaction.CurrentStage);
        Assert.False(transaction.Rollback().Success);
        Assert.Equal("new payload", File.ReadAllText(transaction.Target));
    }

    [Fact]
    public void RemovalCanRollbackBeforeCommit()
    {
        var transaction = Create();
        transaction.BackupTarget();
        Assert.False(File.Exists(transaction.Target));
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
