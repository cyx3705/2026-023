namespace HistoryVulcan.Services.Modules;

/// <summary>A validated module artifact discovered from a validated runtime manifest.</summary>
public sealed record ModuleDiscoveryEntry(
    string Name,
    string Version,
    string PackagePath,
    string ManifestPath,
    string ArtifactPath,
    bool Ui,
    string? DocsPath,
    IReadOnlyList<string> DependencyPaths);

/// <summary>A non-fatal diagnostic produced while discovering runtime module manifests.</summary>
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
