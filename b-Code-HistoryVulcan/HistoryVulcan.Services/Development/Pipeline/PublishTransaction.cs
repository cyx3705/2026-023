namespace HistoryVulcan.Services.Development.Pipeline;

/// <summary>Shares commit and recovery rules for module and flat host promotion.</summary>
internal static class PublishTransaction
{
    internal static void Run(
        string publishRoot,
        IReadOnlyList<string> existing,
        IReadOnlyList<(string Source, string Destination)> incoming,
        TextWriter? log,
        Action<string, string>? move = null,
        Action<string>? delete = null)
    {
        move ??= Move;
        delete ??= Delete;
        // Backups stay on the publication volume so backing up cannot become a partial copy/delete.
        var backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(publishRoot))!,
            ".publish-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        var saved = new List<(string Original, string Backup)>();
        var installed = new List<string>();
        var canDeleteBackup = false;
        try
        {
            foreach (var path in existing)
            {
                var target = Path.Combine(backup, Path.GetFileName(path));
                move(path, target);
                saved.Add((path, target));
            }
            foreach (var (source, destination) in incoming)
            {
                installed.Add(destination);
                move(source, destination);
            }
            canDeleteBackup = true;
        }
        catch (Exception failure)
        {
            try
            {
                foreach (var path in installed)
                    delete(path);
                foreach (var (original, savedPath) in saved)
                    move(savedPath, original);
                canDeleteBackup = true;
            }
            catch (Exception recovery)
            {
                throw new IOException(
                    $"发布失败且回滚未完成，保留备份 {backup}。原始错误: {failure.Message}；恢复错误: {recovery.Message}",
                    new AggregateException(failure, recovery));
            }
            throw;
        }
        finally
        {
            if (canDeleteBackup)
                Cleanup(backup, log, delete);
        }
    }

    internal static void Cleanup(string path, TextWriter? log, Action<string>? delete = null)
    {
        try { (delete ?? Delete)(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var message = $"清理未完成，保留目录 {path}: {ex.Message}";
            if (log != null)
                log.WriteLine(message);
            else
                System.Diagnostics.Trace.TraceWarning(message);
        }
    }

    internal static void Delete(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
        else if (File.Exists(path))
            File.Delete(path);
    }

    internal static void Move(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)),
            StringComparison.OrdinalIgnoreCase);
        if (Directory.Exists(source))
        {
            if (sameVolume)
                Directory.Move(source, destination);
            else
            {
                SnapshotHashes.CopyDirectory(source, destination);
                Directory.Delete(source, recursive: true);
            }
        }
        else if (sameVolume)
            File.Move(source, destination);
        else
        {
            File.Copy(source, destination);
            File.Delete(source);
        }
    }
}
