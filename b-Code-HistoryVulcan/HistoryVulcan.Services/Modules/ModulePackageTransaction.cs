namespace HistoryVulcan.Services.Modules;

/// <summary>Tracks completed filesystem moves so recovery never deletes an untouched package.</summary>
internal sealed class ModulePackageTransaction
{
    internal enum Stage { Preparing, BackedUp, Installed, Committed, RolledBack, RecoveryRequired }

    internal ModulePackageTransaction(string runtimeRoot, string target)
    {
        Target = target;
        Root = RuntimeModulePackageStore.CreateTransactionRoot(runtimeRoot);
    }

    internal string Root { get; }
    internal string Target { get; }
    internal string Staging => Path.Combine(Root, "staging.nupkg");
    internal string Backup => Path.Combine(Root, "backup.nupkg");
    internal Stage CurrentStage { get; private set; }
    private bool _hadPackage;

    internal void BackupTarget()
    {
        if (CurrentStage != Stage.Preparing)
            throw new InvalidOperationException("模块事务不能重复备份。");
        _hadPackage = File.Exists(Target);
        if (_hadPackage)
            File.Move(Target, Backup);
        CurrentStage = Stage.BackedUp;
    }

    internal void InstallStaged()
    {
        if (CurrentStage != Stage.BackedUp)
            throw new InvalidOperationException("模块事务必须先备份再替换。");
        File.Move(Staging, Target);
        CurrentStage = Stage.Installed;
    }

    internal string Commit()
    {
        if (CurrentStage is not (Stage.BackedUp or Stage.Installed))
            throw new InvalidOperationException("模块事务尚未就位，不能提交。");
        CurrentStage = Stage.Committed;
        return RuntimeModulePackageStore.DeleteTransactionRoot(Root);
    }

    internal (bool Success, string Message) Rollback()
    {
        if (CurrentStage == Stage.Committed)
            return (false, $"；事务已经提交，不再回滚。残留目录: {Root}");
        if (CurrentStage == Stage.RecoveryRequired)
            return (false, $"；需要人工恢复，事务目录: {Root}");
        try
        {
            if (CurrentStage is Stage.Installed or Stage.BackedUp)
            {
                if (_hadPackage && !File.Exists(Backup))
                    throw new IOException("旧包备份丢失，拒绝删除当前包。");
                if (CurrentStage == Stage.Installed && File.Exists(Target))
                    File.Delete(Target);
                if (_hadPackage)
                    File.Move(Backup, Target);
            }
            var restored = _hadPackage && CurrentStage is Stage.Installed or Stage.BackedUp;
            CurrentStage = Stage.RolledBack;
            return (true, (restored ? "；旧包已恢复" : "")
                + RuntimeModulePackageStore.DeleteTransactionRoot(Root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CurrentStage = Stage.RecoveryRequired;
            return (false, $"；回滚失败，保留事务目录 {Root}，旧包备份 {Backup}: {ex.Message}");
        }
    }
}
