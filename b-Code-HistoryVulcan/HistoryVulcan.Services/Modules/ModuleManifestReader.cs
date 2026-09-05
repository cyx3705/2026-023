using System.Text.Json;

namespace HistoryVulcan.Services.Modules;

/// <summary>Reads package identity and contained paths independently of directory discovery.</summary>
internal static class ModuleManifestReader
{
    internal const string ManifestType = "HistoryVulcan.Module";
    internal const string ManifestFileName = "module.manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    internal static bool TryReadPackage(
        string package,
        out ModuleDiscoveryEntry entry,
        out string error)
    {
        entry = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(package) || !Path.IsPathFullyQualified(package))
        {
            error = "候选快照目录必须是绝对路径。";
            return false;
        }

        var full = Path.GetFullPath(package.Trim());
        if (!Directory.Exists(full))
        {
            error = $"目录不存在: {full}";
            return false;
        }
        if (!File.Exists(Path.Combine(full, ManifestFileName)))
        {
            error = $"目录里没有 {ManifestFileName}: {full}";
            return false;
        }

        var entries = new List<ModuleDiscoveryEntry>();
        var diagnostics = new List<ModuleDiscoveryDiagnostic>();
        try { DiscoverPackage(full, entries, diagnostics); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            error = $"模块清单路径无效或不可读: {ex.Message}";
            return false;
        }
        if (entries.Count == 1)
        {
            entry = entries[0];
            return true;
        }

        error = diagnostics.Count > 0 ? diagnostics[0].Message : $"{ManifestFileName} 无效。";
        return false;
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
            dependencies));
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
            error = $"路径必须是 模块包目录内的相对路径: {relativePath}";
            return false;
        }

        var packagePath = Path.GetFullPath(package).TrimEnd(Path.DirectorySeparatorChar)
                          + Path.DirectorySeparatorChar;
        resolved = Path.GetFullPath(Path.Combine(packagePath, relativePath));
        if (!resolved.StartsWith(packagePath, StringComparison.OrdinalIgnoreCase))
        {
            error = $"路径越出 模块包目录: {relativePath}";
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

    }
}
