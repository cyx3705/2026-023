namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 从固定运行区的直属 nupkg 发现模块。不扫描子目录，也不把裸 DLL 当成模块。
/// </summary>
internal sealed class RuntimeModuleDiscoverySource : IModuleDiscoverySource
{
    private readonly string _root;

    /// <summary>Creates a scanner for one absolute runtime module directory.</summary>
    public RuntimeModuleDiscoverySource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Runtime module root must be an absolute path.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roots => [_root];

    /// <inheritdoc />
    public ModuleDiscoverySnapshot Discover()
    {
        var diagnostics = new List<ModuleDiscoveryDiagnostic>();
        var candidates = new List<ModuleDiscoveryEntry>();
        if (!Directory.Exists(_root))
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(_root, "missing-root", "运行时模块目录不存在。"));
            return new ModuleDiscoverySnapshot(Roots, candidates, diagnostics);
        }

        string[] packages;
        try
        {
            packages = Directory.GetFiles(_root, "*" + ModuleNupkg.Extension, SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(_root, "root-unreadable", ex.Message));
            return new ModuleDiscoverySnapshot(Roots, candidates, diagnostics);
        }

        foreach (var package in packages)
        {
            if (IsTransientPackageDirectory(Path.GetFileName(package)))
                continue;

            if (TryReadPackage(package, out var entry, out var code, out var error))
                candidates.Add(entry);
            else
                diagnostics.Add(new ModuleDiscoveryDiagnostic(package, code, error));
        }

        var duplicates = candidates
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in duplicates)
        {
            foreach (var entry in candidates.Where(entry =>
                         entry.Name.Equals(duplicate, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new ModuleDiscoveryDiagnostic(
                    entry.ManifestPath,
                    "duplicate-name",
                    $"运行区存在多个名为 {duplicate} 的模块包，所有同名候选均已跳过。"));
            }
        }

        return new ModuleDiscoverySnapshot(
            Roots,
            candidates.Where(entry => !duplicates.Contains(entry.Name))
                .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            diagnostics);
    }

    /// <summary>
    /// 运行区里由发布/回滚工具留下的暂存文件。它们不是活动模块，
    /// 不能被扫描成第二个同名包。
    /// </summary>
    internal static bool IsTransientPackageDirectory(string name)
        => name.StartsWith(".", StringComparison.Ordinal)
           || name.Contains("-rollback-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-backup-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-staging-", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-rollback", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-backup", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-staging", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-rollback" + ModuleNupkg.Extension, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-backup" + ModuleNupkg.Extension, StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-staging" + ModuleNupkg.Extension, StringComparison.OrdinalIgnoreCase);

    internal static bool TryReadPackage(
        string package,
        out ModuleDiscoveryEntry entry,
        out string code,
        out string error)
        => ModuleNupkg.TryRead(package, out entry, out code, out error);
}
