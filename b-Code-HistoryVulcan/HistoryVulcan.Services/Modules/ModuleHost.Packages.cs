using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    /// <summary>
    /// Validates and atomically installs a manifest package into the fixed runtime module directory.
    /// </summary>
    internal CommandResult InstallPackage(string path)
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
        if (!RuntimeModulePackageStore.IsSafeModuleName(package.Name))
            return CommandResult.Fail($"模块名不能安全映射为运行目录: {package.Name}");

        lock (_reloadLock)
        {
            if (_disposed)
                return CommandResult.Fail("模块宿主已停止。");

            Directory.CreateDirectory(runtimeRoot);
            var existing = RuntimeModulePackageStore.FindRuntimePackages(runtimeRoot, package.Name);
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
                && RuntimeModulePackageStore.ChecksumsEqual(source, target))
            {
                var loaded = _current.Modules.FirstOrDefault(module =>
                    module.ModuleName.Equals(package.Name, StringComparison.OrdinalIgnoreCase));
                if (loaded is { Attached: true } || !EnableUiModules)
                    return CommandResult.Ok($"{package.Name} {package.Version} 已安装，内容一致，无需替换。", loaded);
            }

            var transaction = new ModulePackageTransaction(runtimeRoot, target);
            var unloaded = false;
            var wasReady = _ready;
            try
            {
                RuntimeModulePackageStore.CopyPackagePayload(source, transaction.Staging);
                if (!RuntimeModuleDiscoverySource.TryReadPackage(
                        transaction.Staging, out var staged, out _, out validationError))
                {
                    throw new InvalidOperationException($"暂存包复核失败: {validationError}");
                }
                if (!staged.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
                    || !staged.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("暂存包身份在复制过程中发生变化。");
                }

                _watcher.Stop();
                _ready = false;
                if (EnableUiModules
                    && _current.Modules.Any(module =>
                        module.ModuleName.Equals(package.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    MarshalToUi(() => UnloadFromSnapshot(package.Name));
                    unloaded = true;
                }
                transaction.BackupTarget();
                transaction.InstallStaged();

                if (!EnableUiModules)
                {
                    if (!RuntimeModuleDiscoverySource.TryReadPackage(
                            target, out var onDisk, out _, out var diskError)
                        || !onDisk.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
                        || !onDisk.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(onDisk.PackagePath, target, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            $"离线组合不装载 UI 模块，且磁盘包未就位：{diskError ?? onDisk?.Version ?? "(空)"}");
                    }

                    return CommandResult.Ok(
                        $"已写入运行区 {package.Name} {package.Version}: {target}"
                        + "。这是磁盘恢复，不是活宿主热重载。" + transaction.Commit(),
                        onDisk);
                }

                LoadOne(target);
                var loaded = _current.Modules.FirstOrDefault(module =>
                    module.ModuleName.Equals(package.Name, StringComparison.OrdinalIgnoreCase));
                if (loaded == null || !loaded.Attached
                    || !loaded.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(loaded.SourcePath, target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "新包未形成后台确认的运行快照。"
                        + $" 期望 {package.Name} {package.Version} @ {target}；"
                        + $" 实际 {(loaded == null ? "未装载" : $"{loaded.Version} @ {loaded.SourcePath}")}。");
                }

                return CommandResult.Ok(
                    $"已安装并重载 {package.Name} {package.Version}: {target}{transaction.Commit()}", loaded);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException)
            {
                var rollback = transaction.Rollback();
                var recovery = rollback.Message;
                try
                {
                    if (rollback.Success && unloaded && Directory.Exists(target))
                        LoadOne(target);
                }
                catch (Exception reloadEx) { recovery += $"；恢复后重载失败: {reloadEx.Message}"; }
                return CommandResult.Fail($"安装失败: {ex.Message}{recovery}");
            }
            finally
            {
                _ready = wasReady;
                SyncFileWatching();
            }
        }
    }

    /// <summary>Atomically removes a named manifest package from the runtime module directory.</summary>
    internal CommandResult RemovePackage(string name)
    {
        if (!TryGetRuntimeRoot(out var runtimeRoot, out var rootError))
            return CommandResult.Fail(rootError);
        var moduleName = name?.Trim() ?? "";
        if (!RuntimeModulePackageStore.IsSafeModuleName(moduleName))
            return CommandResult.Fail("移除需要合法的模块名。");
        lock (_reloadLock)
        {
            if (_disposed)
                return CommandResult.Fail("模块宿主已停止。");
            var packages = RuntimeModulePackageStore.FindRuntimePackages(runtimeRoot, moduleName);
            if (packages.Count == 0)
                return CommandResult.Fail($"运行区没有模块包 {moduleName}。");
            if (packages.Count > 1)
                return CommandResult.Fail($"运行区存在多个 {moduleName} 包，拒绝不确定移除。");

            var target = packages[0];
            var transaction = new ModulePackageTransaction(runtimeRoot, target);
            var unloaded = false;
            var wasReady = _ready;
            try
            {
                _watcher.Stop();
                _ready = false;
                if (_current.Modules.Any(module =>
                        module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
                {
                    MarshalToUi(() => UnloadFromSnapshot(moduleName));
                    unloaded = true;
                }
                transaction.BackupTarget();
                if (_current.Modules.Any(module =>
                        module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("刷新后模块仍在运行快照中。");
                }

                return CommandResult.Ok($"已从运行区移除模块: {moduleName}{transaction.Commit()}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
            {
                var rollback = transaction.Rollback();
                var recovery = rollback.Message;
                try
                {
                    if (rollback.Success && unloaded && Directory.Exists(target))
                        LoadOne(target);
                }
                catch (Exception reloadEx) { recovery += $"；恢复后重载失败: {reloadEx.Message}"; }
                return CommandResult.Fail($"移除失败: {ex.Message}{recovery}");
            }
            finally
            {
                _ready = wasReady;
                SyncFileWatching();
            }
        }
    }

    /// <summary>
    /// Uninstalls a module package from the fixed AppData runtime directory.
    /// This is the persistent counterpart to <see cref="Unload"/>.
    /// </summary>
    internal CommandResult Uninstall(string name) => RemovePackage(name);

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
}
