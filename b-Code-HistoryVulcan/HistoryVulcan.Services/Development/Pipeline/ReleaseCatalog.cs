using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Development.Pipeline;

internal sealed record PackageLayout(
    string Project,
    string? PublishTargetFramework,
    IReadOnlyList<string> Files);

internal sealed record ValidationStep(
    string Tool,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Configurations,
    string Description,
    string? ModuleOutputRoot,
    string? IsolatedOutputRoot);

internal sealed record ReleaseTarget(
    string Name,
    string Kind,
    string ProjectDirectory,
    string VersionProps,
    string VersionProperty,
    string SourceManifest,
    string SnapshotManifest,
    string IdentityProperty,
    string CandidateDirectory,
    string FormalDirectory,
    string PackageDocuments,
    PackageLayout? Package,
    IReadOnlyList<ValidationStep> Validation,
    string TestProject);

internal static class ReleaseCatalog
{
    public static IReadOnlyList<ReleaseTarget> Load(string registryPath)
    {
        var targets = LoadModules(registryPath).ToDictionary(
            target => target.Name,
            StringComparer.OrdinalIgnoreCase);
        Add(targets, HostTarget());
        return targets.Values.OrderBy(target => target.Name, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<ReleaseTarget> LoadModules(string registryPath)
    {
        using var document = Open(registryPath);
        var targets = new Dictionary<string, ReleaseTarget>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("modules", out var modules))
        {
            foreach (var entry in modules.EnumerateArray())
                Add(targets, ReadModule(entry));
        }

        return targets.Values.OrderBy(target => target.Name, StringComparer.Ordinal).ToList();
    }

    public static ReleaseTarget Require(string registryPath, string name)
        => name.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase)
            ? RequireHost(registryPath, name)
            : LoadModules(registryPath).FirstOrDefault(target =>
                target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
              ?? throw new InvalidOperationException($"{name} 不在发布登记表里。");

    /// <summary>
    /// Resolves the host entry without parsing module entries.
    ///
    /// The module list is intentionally extensible data for the development and module
    /// release pipeline. A malformed or newly added module entry must not prevent a host
    /// candidate from being built or its freeze tag from being checked.
    /// </summary>
    public static ReleaseTarget RequireHost(string registryPath, string name = "HistoryVulcan")
    {
        using var document = Open(registryPath);
        if (!document.RootElement.TryGetProperty("hosts", out var hosts)
            || hosts.ValueKind != JsonValueKind.Array
            || !hosts.EnumerateArray().Any(entry =>
                ReadString(entry, "name").Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"发布登记表没有 {name} 的宿主条目。");
        }

        return HostTarget();
    }


    public static string ReadVersion(string projectRoot, ReleaseTarget target)
    {
        var path = Path.Combine(projectRoot, target.VersionProps);
        var text = File.ReadAllText(path);
        var match = Regex.Match(
            text,
            $"<{Regex.Escape(target.VersionProperty)}>(?<v>\\d+\\.\\d+\\.\\d+)</{Regex.Escape(target.VersionProperty)}>");
        if (!match.Success)
            throw new InvalidOperationException($"版本源必须声明唯一语义化版本 {target.VersionProperty}：{path}");
        return match.Groups["v"].Value;
    }

    private static void Add(IDictionary<string, ReleaseTarget> targets, ReleaseTarget target)
    {
        if (!targets.TryAdd(target.Name, target))
            throw new InvalidOperationException($"重复的发布登记：{target.Name}");
    }

    private static ReleaseTarget HostTarget() => new(
        "HistoryVulcan",
        "host",
        "2026-023-HistoryVulcan",
        "b-Code-HistoryVulcan\\VulcanVersion.props",
        "VulcanVersion",
        "",
        "manifest.json",
        "product",
        "z-Publish",
        "z-Publish",
        "b-Office\\package",
        new PackageLayout(
            "b-Code-HistoryVulcan\\App\\App.csproj",
            null,
            ["HistoryVulcan.exe", "HistoryVulcan.Cli.exe"]),
        [],
        "b-Code-Tests\\HistoryVulcan.Tests\\HistoryVulcan.Tests.csproj");

    private static JsonDocument Open(string registryPath)
    {
        if (!File.Exists(registryPath))
            throw new InvalidOperationException($"找不到发布登记表：{registryPath}");

        var document = JsonDocument.Parse(File.ReadAllText(registryPath));
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || schema.GetInt32() != 1)
        {
            document.Dispose();
            throw new InvalidOperationException("不支持的发布登记表 schema。");
        }

        return document;
    }

    private static ReleaseTarget ReadModule(JsonElement entry)
    {
        var name = ReadString(entry, "name");
        var kind = ReadString(entry, "kind");
        if (string.IsNullOrWhiteSpace(name) || kind != "module")
            throw new InvalidOperationException("登记表每一项必须有非空 name 且 kind=module。");

        return new ReleaseTarget(
            name,
            kind,
            ReadString(entry, "projectDirectory"),
            ReadString(entry, "versionProps"),
            ReadString(entry, "versionProperty"),
            ReadString(entry, "sourceManifest"),
            ReadString(entry, "snapshotManifest", "module.manifest.json"),
            ReadString(entry, "identityProperty", "name"),
            ReadString(entry, "candidateDirectory", "z-Publish"),
            ReadString(entry, "formalDirectory", "z-Publish"),
            ReadString(entry, "packageDocuments", "b-Office\\package"),
            ReadPackage(entry),
            ReadValidation(entry),
            "");
    }

    private static PackageLayout? ReadPackage(JsonElement entry)
    {
        if (!entry.TryGetProperty("package", out var package))
            return null;
        var files = new List<string>();
        if (package.TryGetProperty("files", out var fileArray))
        {
            foreach (var file in fileArray.EnumerateArray())
                files.Add(file.GetString() ?? "");
        }

        var publishTargetFramework = ReadString(package, "publishTargetFramework", fallback: "");
        return new PackageLayout(
            ReadString(package, "project"),
            string.IsNullOrWhiteSpace(publishTargetFramework) ? null : publishTargetFramework,
            files.Where(file => file.Length > 0).ToList());
    }

    private static IReadOnlyList<ValidationStep> ReadValidation(JsonElement entry)
    {
        if (!entry.TryGetProperty("validation", out var steps))
            return [];

        var result = new List<ValidationStep>();
        foreach (var step in steps.EnumerateArray())
        {
            var arguments = new List<string>();
            if (step.TryGetProperty("arguments", out var args))
            {
                foreach (var argument in args.EnumerateArray())
                    arguments.Add(argument.GetString() ?? "");
            }

            var configurations = new List<string>();
            if (step.TryGetProperty("configurations", out var configs))
            {
                foreach (var configuration in configs.EnumerateArray())
                    configurations.Add(configuration.GetString() ?? "");
            }

            if (configurations.Count == 0)
                configurations.Add("");

            result.Add(new ValidationStep(
                ReadString(step, "tool"),
                arguments,
                configurations,
                ReadString(step, "description"),
                step.TryGetProperty("moduleOutputRoot", out var moduleOut) ? moduleOut.GetString() : null,
                step.TryGetProperty("isolatedOutputRoot", out var isolated) ? isolated.GetString() : null));
        }

        return result;
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
        => element.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;
}
