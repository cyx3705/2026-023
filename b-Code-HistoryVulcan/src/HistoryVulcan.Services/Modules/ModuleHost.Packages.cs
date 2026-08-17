using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    /// <summary>
    /// Validates and atomically installs a manifest package into the fixed runtime module directory.
    /// </summary>
    public CommandResult InstallPackage(string path)
    {
        if (!TryGetRuntimeRoot(out var runtimeRoot, out var rootError))
            return CommandResult.Fail(rootError);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return CommandResult.Fail("安装路径必须是绝对的模块包目录。");

        var source = Path.GetFullPath(path.Trim());
        if (!RuntimeModuleDiscoverySource.TryReadPackage(
                source, out var package, out _, out var validationError))
        {
            return CommandResult.Fail($"模块包校验失败: {validationError}");
        }
        if (!IsSafeModuleName(package.Name))
            return CommandResult.Fail($"模块名不能安全映射为运行目录: {package.Name}");

        lock (_reloadLock)
        {
            if (_disposed)
                return CommandResult.Fail("模块宿主已停止。");

            Directory.CreateDirectory(runtimeRoot);
            var existing = FindRuntimePackages(runtimeRoot, package.Name);
            if (existing.Count > 1)
                return CommandResult.Fail($"运行区存在多个 {package.Name} 包，请先人工排除重复目录。");

            var target = Path.Combine(runtimeRoot, package.Name);
            if (existing.Count == 1
                && !existing[0].Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                return CommandResult.Fail(
                    $"{package.Name} 已位于非规范目录 {existing[0]}，拒绝隐式覆盖。");
            }

            if (Directory.Exists(target)
                && RuntimeModuleDiscoverySource.TryReadPackage(
                    target, out var installed, out _, out _)
                && installed.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)
                && ChecksumsEqual(source, target))
            {
                Reload();
                return CommandResult.Ok($"{package.Name} {package.Version} 已安装，内容一致，无需替换。");
            }

            var transactionRoot = CreateTransactionRoot(runtimeRoot);
            var staging = Path.Combine(transactionRoot, "staging");
            var backup = Path.Combine(transactionRoot, "backup");
            try
            {
                CopyPackagePayload(source, staging);
                if (!RuntimeModuleDiscoverySource.TryReadPackage(
                        staging, out var staged, out _, out validationError))
                {
                    return CommandResult.Fail($"暂存包复核失败: {validationError}");
                }
                if (!staged.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
                    || !staged.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Fail("暂存包身份在复制过程中发生变化。");
                }

                _watcher.Stop();
                if (_current.Modules.Any(module =>
                        module.ModuleName.Equals(package.Name, StringComparison.OrdinalIgnoreCase)))
                    UnloadFromSnapshot(package.Name);
                if (Directory.Exists(target))
                    Directory.Move(target, backup);
                Directory.Move(staging, target);

                Reload();
                var loaded = _current.Modules.FirstOrDefault(module =>
                    module.ModuleName.Equals(package.Name, StringComparison.OrdinalIgnoreCase));
                if (loaded == null
                    || !loaded.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(loaded.SourcePath, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("新包未形成后台确认的运行快照。");
                }

                if (Directory.Exists(backup))
                    Directory.Delete(backup, recursive: true);
                return CommandResult.Ok(
                    $"已安装并重载 {package.Name} {package.Version}: {target}", loaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
            {
                var rollback = RestorePackage(target, backup);
                try { Reload(); }
                catch (Exception reloadEx) { rollback += $"；恢复后重载失败: {reloadEx.Message}"; }
                return CommandResult.Fail($"安装失败: {ex.Message}{rollback}");
            }
            finally
            {
                DeleteTransactionRoot(transactionRoot);
                SyncFileWatching();
            }
        }
    }

    /// <summary>Atomically removes a named manifest package from the runtime module directory.</summary>
    public CommandResult RemovePackage(string name)
    {
        if (!TryGetRuntimeRoot(out var runtimeRoot, out var rootError))
            return CommandResult.Fail(rootError);
        var moduleName = name?.Trim() ?? "";
        if (!IsSafeModuleName(moduleName))
            return CommandResult.Fail("移除需要合法的模块名。");
        lock (_reloadLock)
        {
            if (_disposed)
                return CommandResult.Fail("模块宿主已停止。");
            var packages = FindRuntimePackages(runtimeRoot, moduleName);
            if (packages.Count == 0)
                return CommandResult.Fail($"运行区没有模块包 {moduleName}。");
            if (packages.Count > 1)
                return CommandResult.Fail($"运行区存在多个 {moduleName} 包，拒绝不确定移除。");

            var target = packages[0];
            var transactionRoot = CreateTransactionRoot(runtimeRoot);
            var backup = Path.Combine(transactionRoot, "backup");
            try
            {
                _watcher.Stop();
                if (_current.Modules.Any(module =>
                        module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
                    UnloadFromSnapshot(moduleName);
                Directory.Move(target, backup);
                Reload();
                if (_current.Modules.Any(module =>
                        module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("刷新后模块仍在运行快照中。");
                }

                Directory.Delete(backup, recursive: true);
                return CommandResult.Ok($"已从运行区移除模块: {moduleName}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
            {
                var rollback = RestorePackage(target, backup);
                try { Reload(); }
                catch (Exception reloadEx) { rollback += $"；恢复后重载失败: {reloadEx.Message}"; }
                return CommandResult.Fail($"移除失败: {ex.Message}{rollback}");
            }
            finally
            {
                DeleteTransactionRoot(transactionRoot);
                SyncFileWatching();
            }
        }
    }

    private bool TryGetRuntimeRoot(out string root, out string error)
    {
        if (_discoverySource is RuntimeModuleDiscoverySource runtime)
        {
            root = runtime.Roots[0];
            error = "";
            return true;
        }

        root = "";
        error = "当前 ModuleHost 不是固定运行区宿主，不能安装或移除磁盘包。";
        return false;
    }

    private static List<string> FindRuntimePackages(string root, string moduleName)
    {
        if (!Directory.Exists(root))
            return [];
        var matches = new List<string>();
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (RuntimeModuleDiscoverySource.TryReadPackage(directory, out var entry, out _, out _)
                && entry.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                matches.Add(directory);
        }
        return matches;
    }

    private static bool IsSafeModuleName(string name)
        => name.Length > 0
           && name is not "." and not ".."
           && name.Equals(Path.GetFileName(name), StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string CreateTransactionRoot(string runtimeRoot)
    {
        var parent = Directory.GetParent(runtimeRoot)?.FullName
                     ?? throw new InvalidOperationException("运行时模块目录没有父目录。");
        var root = Path.Combine(parent, ".module-transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void CopyPackagePayload(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in RuntimeModuleDiscoverySource.EnumeratePayloadFiles(source)
                     .Append(Path.Combine(source, RuntimeModuleDiscoverySource.ChecksumFileName)))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static bool ChecksumsEqual(string first, string second)
    {
        var left = File.ReadAllText(Path.Combine(first, RuntimeModuleDiscoverySource.ChecksumFileName));
        var right = File.ReadAllText(Path.Combine(second, RuntimeModuleDiscoverySource.ChecksumFileName));
        return left.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Equals(right.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    private static string RestorePackage(string target, string backup)
    {
        try
        {
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            if (Directory.Exists(backup))
                Directory.Move(backup, target);
            return Directory.Exists(target) ? "；旧包已恢复" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"；回滚失败: {ex.Message}";
        }
    }

    private static void DeleteTransactionRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            var parent = Directory.GetParent(root)?.FullName;
            if (parent != null && Directory.Exists(parent)
                               && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch (IOException)
        {
            // Transaction residue is outside the watched directory and can be cleaned on a later run.
        }
        catch (UnauthorizedAccessException)
        {
            // Transaction residue is outside the watched directory and can be cleaned on a later run.
        }
    }
}
