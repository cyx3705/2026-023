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
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, "original.dll"), "old payload");
        File.WriteAllText(Path.Combine(target, "data", "state.txt"), "old state");
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
        Assert.Equal("old state", File.ReadAllText(Path.Combine(transaction.Target, "data", "state.txt")));
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
    public void FailedNewPackageRestoresDataEvenAfterTheNewInstanceWritesIt()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        transaction.InstallStaged();
        File.WriteAllText(Path.Combine(transaction.Target, "data", "state.txt"), "new instance changed it");
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
        Assert.False(Directory.Exists(transaction.Root));
    }

    [Fact]
    public void DataCopyFailureRestoresTheOriginal()
    {
        var transaction = Create();
        Stage(transaction);
        transaction.BackupTarget();
        using (File.Open(Path.Combine(transaction.Backup, "data", "state.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(transaction.InstallStaged);
        Assert.True(transaction.Rollback().Success);
        AssertOriginal(transaction);
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
        Assert.Equal("old state", File.ReadAllText(Path.Combine(transaction.Backup, "data", "state.txt")));
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
        Assert.Equal("old state", File.ReadAllText(Path.Combine(transaction.Target, "data", "state.txt")));
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
