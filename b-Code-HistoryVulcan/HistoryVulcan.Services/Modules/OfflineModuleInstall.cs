namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 不依赖运行中宿主的模块包安装。
/// </summary>
/// <remarks>
/// **这是恢复矩阵里最后一格。**
///
/// 平时换模块靠两条路：经指令总线调 <c>vulcan.module.install</c>，或者直接把包拷进模块槽
/// 让文件监视自己重载。两条都要求宿主**已经起来了**。宿主起不来时——比如某个模块在装载
/// 阶段就抛，或者宿主自己的快照坏了——那两条路一条都不通，而恰恰此刻最需要换包。
///
/// 因此本类刻意**不构建组合根、不装载任何模块、不碰指令总线**：它只做校验和文件事务。
/// 装载路径整个坏掉时它依然成立，这是它存在的全部理由。凡是让它依赖宿主运行时的改动，
/// 都是在拆掉这一格。
///
/// 校验、拷贝与回滚复用 <see cref="RuntimeModulePackageStore"/>，与活宿主的那条编排同源：
/// 「离线装的包和在线装的包不一样」会是一类极难排查的故障。
/// </remarks>
public static class OfflineModuleInstall
{
    /// <summary>安装结果：退出码与一行人读消息。</summary>
    /// <param name="ExitCode">0 成功；2 参数或校验失败；1 文件操作失败。</param>
    /// <param name="Message">人读结果，供 CLI 直接打印。</param>
    public readonly record struct Result(int ExitCode, string Message);

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 处的模块包安装进 <paramref name="runtimeRoot"/>。
    /// </summary>
    /// <remarks>
    /// 与在线安装的差别只有一处：不卸载、不重载、不核对快照。若此刻恰好有宿主在运行，
    /// 它的文件监视会看到目录变化并自行重载——**但不要依赖这一点**：
    /// 正在被装载的程序集处于文件锁定状态，替换会失败并回滚。
    /// 换活着的宿主里的模块，请走 <c>vulcan.module.install</c>。
    /// </remarks>
    public static Result Install(string runtimeRoot, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            return new Result(2, "安装路径必须是绝对的模块包目录。");

        var source = Path.GetFullPath(sourcePath.Trim());
        if (!RuntimeModuleDiscoverySource.TryReadPackage(
                source, out var package, out _, out var validationError))
        {
            return new Result(2, $"模块包校验失败: {validationError}");
        }

        if (!RuntimeModulePackageStore.IsSafeModuleName(package.Name))
            return new Result(2, $"模块名不能安全映射为运行目录: {package.Name}");

        Directory.CreateDirectory(runtimeRoot);
        var existing = RuntimeModulePackageStore.FindRuntimePackages(runtimeRoot, package.Name);
        if (existing.Count > 1)
            return new Result(2, $"运行区存在多个 {package.Name} 包，请先人工排除重复目录。");

        var target = Path.Combine(runtimeRoot, package.Name);
        if (existing.Count == 1
            && !existing[0].Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            return new Result(2, $"{package.Name} 已位于非规范目录 {existing[0]}，拒绝隐式覆盖。");
        }

        if (Directory.Exists(target)
            && RuntimeModuleDiscoverySource.TryReadPackage(target, out var installed, out _, out _)
            && installed.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)
            && RuntimeModulePackageStore.ChecksumsEqual(source, target))
        {
            return new Result(0, $"{package.Name} {package.Version} 已安装，内容一致，无需替换。");
        }

        var transactionRoot = RuntimeModulePackageStore.CreateTransactionRoot(runtimeRoot);
        var staging = Path.Combine(transactionRoot, "staging");
        var backup = Path.Combine(transactionRoot, "backup");
        try
        {
            // 先在事务目录里拼出完整新包并复核，再动运行区：
            // 拷到一半失败也不会让运行区停在半个包上。
            RuntimeModulePackageStore.CopyPackagePayload(source, staging);
            if (!RuntimeModuleDiscoverySource.TryReadPackage(
                    staging, out var staged, out _, out validationError))
            {
                return new Result(2, $"暂存包复核失败: {validationError}");
            }

            if (!staged.Name.Equals(package.Name, StringComparison.OrdinalIgnoreCase)
                || !staged.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase))
            {
                return new Result(2, "暂存包身份在复制过程中发生变化。");
            }

            if (Directory.Exists(target))
                Directory.Move(target, backup);
            Directory.Move(staging, target);
            RuntimeModulePackageStore.PreserveMutableData(backup, target);

            if (Directory.Exists(backup))
                Directory.Delete(backup, recursive: true);
            return new Result(0, $"已安装 {package.Name} {package.Version}: {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目标目录被占用是这条路径上最常见的失败：宿主正在运行并且已经装载了这个模块。
            var rollback = RuntimeModulePackageStore.RestorePackage(target, backup);
            return new Result(1, $"安装失败: {ex.Message}{rollback}");
        }
        finally
        {
            RuntimeModulePackageStore.DeleteTransactionRoot(transactionRoot);
        }
    }
}
