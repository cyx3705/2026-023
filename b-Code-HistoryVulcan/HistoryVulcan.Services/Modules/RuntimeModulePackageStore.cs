using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 运行区模块包的校验与文件事务原语。
/// </summary>
/// <remarks>
/// 这些方法原先是 <see cref="ModuleHost"/> 的私有静态成员。抽出来是因为**装包有两条编排**：
///
/// - <c>ModuleHost.InstallPackage</c>：在活着的宿主里换包，要先卸载旧模块释放文件锁，
///   换完还要重载并核对新快照；
/// - <c>OfflineModuleInstall</c>：宿主没起来（或起不来）时按文件系统换包，
///   没有快照可卸、也没有快照可核。
///
/// 两条编排不同，但**校验、拷贝与回滚必须是同一份**——这几步一旦分叉，
/// 代价是用户的模块目录。编排各自保留，原语共用。
/// </remarks>
internal static class RuntimeModulePackageStore
{
    internal static List<string> FindRuntimePackages(string root, string moduleName)
    {
        if (!Directory.Exists(root))
            return [];
        var matches = new List<string>();
        foreach (var package in Directory.GetFiles(root, "*" + ModuleNupkg.Extension, SearchOption.TopDirectoryOnly))
        {
            if (RuntimeModuleDiscoverySource.IsTransientPackageDirectory(Path.GetFileName(package)))
                continue;
            if (RuntimeModuleDiscoverySource.TryReadPackage(package, out var entry, out _, out _)
                && entry.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                matches.Add(package);
        }
        return matches;
    }

    internal static bool IsSafeModuleName(string name)
        => name.Length > 0
           && name is not "." and not ".."
           && name.Equals(Path.GetFileName(name), StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    internal static bool TryValidateTargetOwner(string target, string moduleName, out string error)
    {
        error = "";
        // 损坏的目标仍允许恢复；有效的邻居包不能因为文件名碰巧相同而被覆盖。
        if (File.Exists(target)
            && RuntimeModuleDiscoverySource.TryReadPackage(target, out var installed, out _, out _)
            && !installed.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
        {
            error = $"目标文件 {target} 属于模块 {installed.Name}，拒绝用 {moduleName} 覆盖。";
            return false;
        }

        return true;
    }

    internal static string CreateTransactionRoot(string runtimeRoot)
    {
        var parent = Directory.GetParent(runtimeRoot)?.FullName
                     ?? throw new InvalidOperationException("运行时模块目录没有父目录。");
        var root = Path.Combine(parent, ".module-transactions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static void CopyPackagePayload(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        File.Copy(source, destination, overwrite: false);
    }

    internal static bool ChecksumsEqual(string first, string second)
        => ModuleNupkg.SameContent(first, second);

    internal static string DeleteTransactionRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            var parent = Directory.GetParent(root)?.FullName;
            if (parent != null && Directory.Exists(parent)
                               && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"；清理未完成，残留目录 {root}: {ex.Message}";
        }
    }
}
