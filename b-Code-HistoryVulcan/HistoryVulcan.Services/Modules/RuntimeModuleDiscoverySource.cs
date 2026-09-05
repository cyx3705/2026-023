using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// Discovers manifest packages from the direct children of a fixed runtime module directory.
/// </summary>
public sealed class RuntimeModuleDiscoverySource : IModuleDiscoverySource
{
    /// <summary>The integrity manifest required at the root of every module package.</summary>
    internal const string ChecksumFileName = "SHA256SUMS";
    /// <summary>Runtime-owned state is kept beside the immutable package payload.</summary>
    internal const string MutableDataDirectoryName = "data";

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
            packages = Directory.GetDirectories(_root, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(_root, "root-unreadable", ex.Message));
            return new ModuleDiscoverySnapshot(Roots, candidates, diagnostics);
        }

        foreach (var package in packages)
        {
            // Recovery residues must not compete with the active package's identity.
            if (IsTransientPackageDirectory(Path.GetFileName(package)))
                continue;

            if (TryReadPackage(package, out var entry, out var code, out var error))
                candidates.Add(entry);
            else if (File.Exists(Path.Combine(package, ModuleManifestReader.ManifestFileName)))
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
    /// 运行区里由发布/回滚工具留下的暂存目录。它们不是活动模块，
    /// 不能被扫描成第二个同名包。
    /// </summary>
    internal static bool IsTransientPackageDirectory(string name)
        => name.StartsWith(".", StringComparison.Ordinal)
           || name.Contains("-rollback-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-backup-", StringComparison.OrdinalIgnoreCase)
           || name.Contains("-staging-", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-rollback", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-backup", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith("-staging", StringComparison.OrdinalIgnoreCase);

    internal static bool TryReadPackage(
        string package,
        out ModuleDiscoveryEntry entry,
        out string code,
        out string error)
    {
        if (!ModuleManifestReader.TryReadPackage(package, out entry, out error))
        {
            code = "invalid-manifest";
            return false;
        }

        if (IsMutablePath(Path.GetRelativePath(package, entry.ArtifactPath)))
        {
            entry = null!;
            code = "invalid-artifact";
            error = "artifact 不得位于模块运行态 data/ 目录。";
            return false;
        }

        if (entry.DocsPath != null && IsMutablePath(Path.GetRelativePath(package, entry.DocsPath)))
        {
            entry = null!;
            code = "invalid-docs";
            error = "docs 不得位于模块运行态 data/ 目录。";
            return false;
        }

        if (entry.DependencyPaths.Any(path => IsMutablePath(Path.GetRelativePath(package, path))))
        {
            entry = null!;
            code = "invalid-dependency";
            error = "依赖不得位于模块运行态 data/ 目录。";
            return false;
        }

        if (!TryValidateChecksums(package, out error))
        {
            entry = null!;
            code = "invalid-checksum";
            return false;
        }

        code = "";
        return true;
    }

    internal static bool TryValidateChecksums(string package, out string error)
    {
        error = "";
        var root = Path.GetFullPath(package).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var checksumPath = Path.Combine(root, ChecksumFileName);
        if (!File.Exists(checksumPath))
        {
            error = $"缺少 {ChecksumFileName}。";
            return false;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    error = $"模块包不允许目录链接: {Path.GetRelativePath(root, directory)}";
                    return false;
                }
            }

            var expected = EnumeratePayloadFiles(root)
                .ToDictionary(
                    path => NormalizeRelative(root, path),
                    path => path,
                    StringComparer.OrdinalIgnoreCase);
            var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(checksumPath))
            {
                var match = Regex.Match(line, "^([0-9A-Fa-f]{64})  (.+)$", RegexOptions.CultureInvariant);
                if (!match.Success)
                {
                    error = $"SHA256SUMS 行格式无效: {line}";
                    return false;
                }

                var relative = match.Groups[2].Value.Replace('\\', '/');
                if (relative.Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase)
                    || IsMutablePath(relative)
                    || Path.IsPathRooted(relative))
                {
                    error = $"SHA256SUMS 包含保留、运行态或绝对路径: {relative}";
                    return false;
                }

                var target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(target)
                    || !declared.TryAdd(relative, match.Groups[1].Value.ToUpperInvariant()))
                {
                    error = $"SHA256SUMS 路径越界、缺失或重复: {relative}";
                    return false;
                }
            }

            if (declared.Count != expected.Count
                || expected.Keys.Any(path => !declared.ContainsKey(path)))
            {
                error = "SHA256SUMS 未完整覆盖候选包内容（history/、data/ 与自身除外）。";
                return false;
            }

            foreach (var (relative, path) in expected)
            {
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                if (!actual.Equals(declared[relative], StringComparison.OrdinalIgnoreCase))
                {
                    error = $"SHA-256 不匹配: {relative}";
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static IEnumerable<string> EnumeratePayloadFiles(string root)
    {
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelative(root, path);
            if (relative.Equals(ChecksumFileName, StringComparison.OrdinalIgnoreCase)
                || IsMutablePath(relative))
                continue;
            yield return path;
        }
    }

    private static string NormalizeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool IsHistoryPath(string relative)
        => relative.Equals("history", StringComparison.OrdinalIgnoreCase)
           || relative.StartsWith("history/", StringComparison.OrdinalIgnoreCase);

    internal static bool IsMutablePath(string relative)
    {
        var normalized = relative.Replace('\\', '/');
        return IsHistoryPath(normalized)
               || normalized.Equals(MutableDataDirectoryName, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(MutableDataDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }
}
