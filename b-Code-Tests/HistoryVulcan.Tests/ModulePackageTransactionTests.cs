using HistoryVulcan.Services.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ModulePackageTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vulcan-transaction-" + Guid.NewGuid().ToString("N"));

    private ModulePackageTransaction Create()
    {
        var runtime = Path.Combine(_root, "Modules");
        var target = Path.Combine(runtime, "Sample");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "original.dll"), "old payload");
        return new ModulePackageTransaction(runtime, target);
    }

    private static void Stage(ModulePackageTransaction transaction)
    {
        Directory.CreateDirectory(transaction.Staging);
        File.WriteAllText(Path.Combine(transaction.Staging, "new.dll"), "new payload");
    }

    private static void AssertOriginal(ModulePackageTransaction transaction)
    {
        Assert.Equal("old payload", File.ReadAllText(Path.Combine(transaction.Target, "original.dll")));
        Assert.False(File.Exists(Path.Combine(transaction.Target, "new.dll")));
    }

    [Fact]
    public void StagingAndBackupFailuresLeaveTheEntireOriginalUntouched()
    {
        var transaction = Create();
        Directory.CreateDirectory(transaction.Staging);
        File.WriteAllText(Path.Combine(transaction.Staging, "partial"), "incomplete");
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);

        transaction = new ModulePackageTransaction(Path.GetDirectoryName(transaction.Target)!, transaction.Target);
        using var locked = File.Open(Path.Combine(transaction.Target, "original.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
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
        File.WriteAllText(Path.Combine(transaction.Target, "written-by-new-instance.txt"), "stray");
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
        Assert.False(File.Exists(Path.Combine(transaction.Target, "written-by-new-instance.txt")));
        Assert.False(Directory.Exists(transaction.Root));
    }

    [Fact]
    public void InstallReplacesTheWholeSlotAndCarriesNothingForward()
    {
        // 6.0.0：模块数据在宿主给的 ModuleData/<名>/，槽位里没有要保留的东西；旧槽里的任何文件都不跟到新包。
        var transaction = Create();
        File.WriteAllText(Path.Combine(transaction.Target, "data-left-by-old-version.txt"), "old");
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        transaction.Commit();
        Assert.Equal("new.dll", Path.GetFileName(Assert.Single(Directory.GetFiles(transaction.Target))));
    }

    [Fact]
    public void RollbackFailureRetainsTheBackupAndReportsItsPath()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        using var locked = File.Open(Path.Combine(transaction.Target, "new.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var rollback = transaction.Rollback();
        Assert.False(rollback.Success);
        Assert.Contains(transaction.Backup, rollback.Message, StringComparison.Ordinal);
        Assert.Equal("old payload", File.ReadAllText(Path.Combine(transaction.Backup, "original.dll")));
        Assert.Equal(ModulePackageTransaction.Stage.RecoveryRequired, transaction.CurrentStage);
    }

    [Fact]
    public void CleanupFailureDoesNotUndoACommittedInstallation()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        using var locked = File.Open(Path.Combine(transaction.Backup, "original.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Contains(transaction.Root, transaction.Commit(), StringComparison.Ordinal);
        Assert.Equal(ModulePackageTransaction.Stage.Committed, transaction.CurrentStage);
        Assert.False(transaction.Rollback().Success);
        Assert.Equal("new payload", File.ReadAllText(Path.Combine(transaction.Target, "new.dll")));
    }

    [Fact]
    public void RemovalCanRollbackBeforeCommit()
    {
        var transaction = Create();
        transaction.BackupTarget();
        Assert.False(Directory.Exists(transaction.Target));
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
