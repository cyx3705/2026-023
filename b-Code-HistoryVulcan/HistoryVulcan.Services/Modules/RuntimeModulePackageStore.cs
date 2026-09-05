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
        foreach (var directory in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (RuntimeModuleDiscoverySource.TryReadPackage(directory, out var entry, out _, out _)
                && entry.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                matches.Add(directory);
        }
        return matches;
    }

    internal static bool IsSafeModuleName(string name)
        => name.Length > 0
           && name is not "." and not ".."
           && name.Equals(Path.GetFileName(name), StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

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

    /// <summary>Copies runtime state while keeping the original available for rollback.</summary>
    internal static void PreserveMutableData(string backup, string target)
    {
        var source = Path.Combine(backup, RuntimeModuleDiscoverySource.MutableDataDirectoryName);
        if (!Directory.Exists(source))
            return;

        var destination = Path.Combine(target, RuntimeModuleDiscoverySource.MutableDataDirectoryName);
        if (Directory.Exists(destination))
            throw new IOException($"运行态目录已存在，无法保留旧数据: {destination}");

        CopyData(source, destination);
    }

    private static void CopyData(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"运行数据目录不能是重解析点: {source}");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"运行数据文件不能是重解析点: {file}");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in Directory.GetDirectories(source))
            CopyData(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    internal static bool ChecksumsEqual(string first, string second)
    {
        var left = File.ReadAllText(Path.Combine(first, RuntimeModuleDiscoverySource.ChecksumFileName));
        var right = File.ReadAllText(Path.Combine(second, RuntimeModuleDiscoverySource.ChecksumFileName));
        return left.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Equals(right.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

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
