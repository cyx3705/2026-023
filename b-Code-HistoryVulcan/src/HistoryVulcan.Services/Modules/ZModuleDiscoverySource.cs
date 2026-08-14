using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Modules;

/// <summary>A validated module artifact discovered from an explicit Z-level manifest.</summary>
public sealed record ModuleDiscoveryEntry(
    string Name,
    string Version,
    string PackagePath,
    string ManifestPath,
    string ArtifactPath,
    bool Ui,
    string? DocsPath,
    IReadOnlyList<string> DependencyPaths,
    string? McpExposure);

/// <summary>A non-fatal diagnostic produced while discovering Z-level module manifests.</summary>
public sealed record ModuleDiscoveryDiagnostic(string Path, string Code, string Message);

/// <summary>The immutable result of one module discovery scan.</summary>
public sealed record ModuleDiscoverySnapshot(
    IReadOnlyList<string> Roots,
    IReadOnlyList<ModuleDiscoveryEntry> Modules,
    IReadOnlyList<ModuleDiscoveryDiagnostic> Diagnostics);

/// <summary>Discovers module artifacts without loading their assemblies.</summary>
public interface IModuleDiscoverySource
{
    /// <summary>Configured absolute discovery roots.</summary>
    IReadOnlyList<string> Roots { get; }

    /// <summary>Scans the configured roots and validates explicit module manifests.</summary>
    ModuleDiscoverySnapshot Discover();
}

/// <summary>
/// Discovers <c>HistoryVulcan.Module</c> manifests from direct <c>z-*</c> children of projects.
/// Folder names locate candidates only; the manifest is the authority for module identity.
/// </summary>
public sealed class ZModuleDiscoverySource : IModuleDiscoverySource
{
    /// <summary>The only manifest type accepted as a HistoryVulcan module.</summary>
    public const string ManifestType = "HistoryVulcan.Module";

    /// <summary>The root-level manifest file name used by Z module packages.</summary>
    public const string ManifestFileName = "module.manifest.json";

    /// <summary>
    /// 参与模块发现的项目目录名模式。
    ///
    /// 发现根通常就是整个项目库，而库里绝大多数编号项目（课程设计、实验、工具链等）
    /// 与本体系无关。用模式而不是白名单收窄：新模块只要按命名规范建目录就自动纳入，
    /// 不需要有人记得回来改配置——白名单迟早会漏更新，模式不会。
    ///
    /// 匹配 &lt;编号&gt;-History&lt;名字&gt; 形态；命名规范由 OneHistory 的
    /// 「目录与命名规范」定义，宿主的 <c>ModuleDomainNaming.ToDomain</c> 也依赖同一约定。
    /// </summary>
    public const string ProjectDirectoryPattern = "*-History*";

    /// <summary>当前 OneHistory 项目库根。自动发现与旧 Vesta 路径迁移都落到这里。</summary>
    public const string DefaultLibraryRoot = @"C:\OneHistory\HistoryClio";

    /// <summary>已退役的共享项目库根；配置里若仍指向它则改写到 <see cref="DefaultLibraryRoot"/>。</summary>
    public const string LegacyVestaLibrary = @"C:\OneHistory\HistoryVesta";

    /// <summary>自动发现失败时的说明：库根靠编号项目目录识别，不靠裸仓哨兵。</summary>
    public const string AutomaticRootNotFound =
        "未能向上找到含多个编号项目目录的项目库根。";

    private const int MinimumNumberedProjects = 2;

    private static readonly Regex NumberedProjectName =
        new(@"^\d{4}-\d{3}-.+", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyList<string> _roots;

    /// <summary>Creates a scanner for one or more absolute project-library roots.</summary>
    public ZModuleDiscoverySource(IEnumerable<string> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var configuredRoots = roots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (configuredRoots.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("Module discovery roots must be absolute paths.", nameof(roots));
        _roots = configuredRoots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_roots.Count == 0)
            throw new ArgumentException("至少需要一个模块发现根。", nameof(roots));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Roots => _roots;

    /// <summary>
    /// Walks upward until a directory contains at least two numbered project folders
    /// (<c>YYYY-NNN-*</c>). Does not open <c>.git</c> and does not use a bare-repo sentinel.
    /// </summary>
    public static string? FindAutomaticRoot(string startPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startPath);
        var path = Path.GetFullPath(startPath);
        var directory = File.Exists(path) ? Directory.GetParent(path) : new DirectoryInfo(path);
        while (directory != null)
        {
            if (LooksLikeLibraryRoot(directory.FullName))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// Rewrites a configured discovery root: missing or retired Vesta paths become
    /// <see cref="DefaultLibraryRoot"/> when that library exists.
    /// </summary>
    public static string CoerceConfiguredRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(LegacyVestaLibrary, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(full, "HistoryVesta.git")))
        {
            if (LooksLikeLibraryRoot(DefaultLibraryRoot))
                return DefaultLibraryRoot;
        }

        return full;
    }

    /// <summary>Walks from the process location, then the well-known Clio library.</summary>
    public static string? ResolveAutomaticRoot()
        => FindAutomaticRoot(AppContext.BaseDirectory)
           ?? FindAutomaticRoot(Environment.CurrentDirectory)
           ?? FindAutomaticRoot(DefaultLibraryRoot);

    private static bool LooksLikeLibraryRoot(string directory)
    {
        try
        {
            var counted = 0;
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    continue;
                if (!NumberedProjectName.IsMatch(Path.GetFileName(child)))
                    continue;
                if (++counted >= MinimumNumberedProjects)
                    return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <inheritdoc />
    public ModuleDiscoverySnapshot Discover()
    {
        var diagnostics = new List<ModuleDiscoveryDiagnostic>();
        var candidates = new List<ModuleDiscoveryEntry>();
        foreach (var root in _roots)
            DiscoverRoot(root, candidates, diagnostics);

        var duplicateNames = candidates
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in duplicateNames)
        {
            foreach (var entry in candidates.Where(entry =>
                         entry.Name.Equals(duplicate, StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new ModuleDiscoveryDiagnostic(
                    entry.ManifestPath,
                    "duplicate-name",
                    $"模块名 {duplicate} 在多个 Z 目录中重复，所有同名候选均已跳过。"));
            }
        }

        var modules = candidates
            .Where(entry => !duplicateNames.Contains(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ModuleDiscoverySnapshot(_roots, modules, diagnostics);
    }

    private static void DiscoverRoot(
        string root,
        ICollection<ModuleDiscoveryEntry> entries,
        ICollection<ModuleDiscoveryDiagnostic> diagnostics)
    {
        if (!Directory.Exists(root))
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(root, "missing-root", "模块发现根不存在。"));
            return;
        }

        IEnumerable<string> projects;
        try { projects = Directory.EnumerateDirectories(root, ProjectDirectoryPattern).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(root, "root-unreadable", ex.Message));
            return;
        }

        foreach (var project in projects)
        {
            IEnumerable<string> packages;
            try
            {
                packages = Directory.EnumerateDirectories(project, "z-*", SearchOption.TopDirectoryOnly)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new ModuleDiscoveryDiagnostic(project, "project-unreadable", ex.Message));
                continue;
            }

            foreach (var package in packages)
                DiscoverPackage(package, entries, diagnostics);
        }
    }

    private static void DiscoverPackage(
        string package,
        ICollection<ModuleDiscoveryEntry> entries,
        ICollection<ModuleDiscoveryDiagnostic> diagnostics)
    {
        var manifestPath = Path.Combine(package, ManifestFileName);
        if (!File.Exists(manifestPath))
            return;

        Manifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifestPath), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(manifestPath, "invalid-json", ex.Message));
            return;
        }

        if (manifest == null || manifest.SchemaVersion != 1)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(
                manifestPath, "invalid-schema", "schemaVersion 必须为 1。"));
            return;
        }
        if (!string.Equals(manifest.Type, ManifestType, StringComparison.Ordinal))
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(
                manifestPath, "invalid-type", $"type 必须为 {ManifestType}。"));
            return;
        }
        if (string.IsNullOrWhiteSpace(manifest.Name)
            || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.Artifact)
            || manifest.Ui is null)
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(
                manifestPath, "missing-field", "name、version、artifact 和 ui 均为必填字段。"));
            return;
        }

        if (!TryResolveContainedFile(package, manifest.Artifact, out var artifact, out var error))
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(manifestPath, "invalid-artifact", error));
            return;
        }

        string? docs = null;
        if (!string.IsNullOrWhiteSpace(manifest.Docs)
            && !TryResolveContainedFile(package, manifest.Docs, out docs, out error))
        {
            diagnostics.Add(new ModuleDiscoveryDiagnostic(manifestPath, "invalid-docs", error));
            return;
        }

        var dependencies = new List<string>();
        foreach (var dependency in manifest.Deps ?? [])
        {
            if (!TryResolveContainedFile(package, dependency, out var resolved, out error))
            {
                diagnostics.Add(new ModuleDiscoveryDiagnostic(manifestPath, "invalid-dependency", error));
                return;
            }
            dependencies.Add(resolved);
        }

        entries.Add(new ModuleDiscoveryEntry(
            manifest.Name.Trim(),
            manifest.Version.Trim(),
            Path.GetFullPath(package),
            Path.GetFullPath(manifestPath),
            artifact,
            manifest.Ui.Value,
            docs,
            dependencies,
            manifest.McpExposure));
    }

    private static bool TryResolveContainedFile(
        string package,
        string relativePath,
        out string resolved,
        out string error)
    {
        resolved = "";
        error = "";
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            error = $"路径必须是 Z 目录内的相对路径: {relativePath}";
            return false;
        }

        var packagePath = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        resolved = Path.GetFullPath(Path.Combine(packagePath, relativePath));
        if (!resolved.StartsWith(packagePath, StringComparison.OrdinalIgnoreCase))
        {
            error = $"路径越出 Z 目录: {relativePath}";
            return false;
        }
        if (!File.Exists(resolved))
        {
            error = $"入口不存在: {relativePath}";
            return false;
        }
        return true;
    }

    private sealed class Manifest
    {
        public int SchemaVersion { get; init; }
        public string? Type { get; init; }
        public string? Name { get; init; }
        public string? Version { get; init; }
        public string? Artifact { get; init; }
        public bool? Ui { get; init; }
        public string? Docs { get; init; }
        public IReadOnlyList<string>? Deps { get; init; }
        public string? McpExposure { get; init; }
    }
}
