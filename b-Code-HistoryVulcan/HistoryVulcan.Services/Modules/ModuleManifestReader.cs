namespace HistoryVulcan.Services.Modules;

/// <summary>模块包身份。7.0.0 起包是 nupkg，清单在包内。</summary>
internal static class ModuleManifestReader
{
    internal const string ManifestType = "HistoryVulcan.Module";
    internal const string ManifestFileName = "module.manifest.json";

    internal static bool TryReadPackage(
        string package,
        out ModuleDiscoveryEntry entry,
        out string error)
        => ModuleNupkg.TryRead(package, out entry, out _, out error);
}
