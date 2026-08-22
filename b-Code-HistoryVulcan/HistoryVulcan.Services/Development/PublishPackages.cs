using System.IO;
using System.Text.Json;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 发布包的定位约定：候选包在哪、历史归档在哪、目录怎么命名。
/// </summary>
/// <remarks>
/// 随开发路线迁入宿主（4.6.0）并提为公开面：这是**体系共用的约定**而非管线私有实现，
/// HistoryDiana 的 z 文档通道也按它枚举当前包。两边各写一份，
/// 迟早在「z-Publish 还是别的名字」这种问题上分叉。
/// </remarks>
public static class PublishPackages
{
    /// <summary>发布目录名。</summary>
    public const string PublishDirectoryName = "z-Publish";
    /// <summary>历史发布归档的子目录名。</summary>
    public const string HistoryDirectoryName = "history";

    /// <summary>按模块名与版本拼出候选包目录名。</summary>
    public static string PackageDirectoryName(string name, string version)
        => $"{name}-v{version}";

    /// <summary>解析项目当前的候选包目录；找不到或有歧义时抛出。</summary>
    public static string ResolveCurrent(string projectRoot, string expectedName)
    {
        if (expectedName.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase))
            return ResolveHostSnapshot(projectRoot);

        return ResolveVersionedCurrent(projectRoot, expectedName);
    }

    private static string ResolveVersionedCurrent(string projectRoot, string expectedName)
    {
        var publishRoot = Path.Combine(projectRoot, PublishDirectoryName);
        var packages = EnumerateVersioned(publishRoot)
            .Where(package => package.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return packages.Count switch
        {
            1 => packages[0].Path,
            0 => throw new DirectoryNotFoundException(
                $"{publishRoot} 中没有 {expectedName}-v<版本> 候选包。"),
            _ => throw new InvalidOperationException(
                $"{publishRoot} 中存在多个 {expectedName} 候选包，发布区必须只保留一个当前候选。"),
        };
    }

    /// <summary>解析宿主快照目录。</summary>
    public static string ResolveHostSnapshot(string vulcanProjectRoot)
    {
        var publishRoot = Path.Combine(vulcanProjectRoot, PublishDirectoryName);
        // Vulcan 4.0.0 keeps the current host payload flat at z-Publish/host.
        // Prefer it over any migration-era HistoryVulcan-v* directory so a
        // stale archive can never become the compile-time dependency.
        if (TryReadFlatHost(publishRoot, out _))
        {
            return Path.GetFullPath(publishRoot);
        }

        var flatShapePresent = Directory.Exists(Path.Combine(publishRoot, "host"))
            || File.Exists(Path.Combine(publishRoot, "manifest.json"))
            || File.Exists(Path.Combine(publishRoot, "SHA256SUMS"));
        if (flatShapePresent)
        {
            throw new InvalidOperationException(
                $"HistoryVulcan flat host snapshot is incomplete or invalid: {publishRoot}");
        }

        // Keep the versioned resolver only as a migration fallback. New 4.0.0
        // publishes never create this shape at the current z-Publish root.
        return ResolveVersionedCurrent(vulcanProjectRoot, "HistoryVulcan");
    }

    /// <summary>枚举发布根下的全部当前包。</summary>
    public static IReadOnlyList<PublishedPackage> EnumerateCurrent(string publishRoot)
    {
        if (!Directory.Exists(publishRoot))
            return [];

        var result = new List<PublishedPackage>();
        var hasFlatHost = TryReadFlatHost(publishRoot, out var flatHost);
        if (hasFlatHost)
            result.Add(flatHost);

        // Once the 4.0.0 flat host exists, a migration-era HistoryVulcan-v*
        // directory at the same level is stale and must not become a second
        // current documentation channel. It belongs under history/.
        result.AddRange(EnumerateVersioned(publishRoot)
            .Where(package => !hasFlatHost
                || !package.Name.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase)));
        return result
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<PublishedPackage> EnumerateVersioned(string publishRoot)
    {
        if (!Directory.Exists(publishRoot))
            return [];

        var result = new List<PublishedPackage>();
        foreach (var directory in Directory.GetDirectories(publishRoot, "History*-v*", SearchOption.TopDirectoryOnly))
        {
            if (TryRead(directory, out var package))
                result.Add(package);
        }

        return result
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryReadFlatHost(string publishRoot, out PublishedPackage package)
    {
        package = default!;
        var manifestPath = Path.Combine(publishRoot, "manifest.json");
        var sumsPath = Path.Combine(publishRoot, "SHA256SUMS");
        var hostExecutable = Path.Combine(publishRoot, "host", "HistoryVulcan.exe");
        if (!File.Exists(manifestPath) || !File.Exists(sumsPath) || !File.Exists(hostExecutable))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("product", out var product)
                || !root.TryGetProperty("version", out var version)
                || product.GetString()?.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase) != true
                || version.GetString() is not { Length: > 0 } versionValue)
            {
                return false;
            }

            package = new PublishedPackage(
                "HistoryVulcan",
                versionValue,
                Path.GetFullPath(publishRoot),
                Path.GetFullPath(manifestPath));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>读取一个发布包目录的身份；不是有效包时返回 false。</summary>
    public static bool TryRead(string path, out PublishedPackage package)
    {
        package = default!;
        var moduleManifest = Path.Combine(path, "module.manifest.json");
        var hostManifest = Path.Combine(path, "manifest.json");
        var manifest = File.Exists(moduleManifest)
            ? moduleManifest
            : File.Exists(hostManifest) ? hostManifest : null;
        if (manifest == null || !File.Exists(Path.Combine(path, "SHA256SUMS")))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var identityProperty = manifest.Equals(moduleManifest, StringComparison.OrdinalIgnoreCase)
                ? "name"
                : "product";
            if (!document.RootElement.TryGetProperty(identityProperty, out var identity)
                || !document.RootElement.TryGetProperty("version", out var versionElement)
                || identity.GetString() is not { Length: > 0 } name
                || versionElement.GetString() is not { Length: > 0 } version)
            {
                return false;
            }

            var expectedDirectory = PackageDirectoryName(name, version);
            if (!Path.GetFileName(path).Equals(expectedDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

            package = new PublishedPackage(name, version, Path.GetFullPath(path), manifest);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>一个已发布包的身份与位置。</summary>
    public sealed record PublishedPackage(string Name, string Version, string Path, string ManifestPath);
}
