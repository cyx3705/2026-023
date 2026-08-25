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
        if (!File.Exists(registryPath))
            throw new InvalidOperationException($"找不到发布登记表：{registryPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(registryPath));
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidOperationException("不支持的发布登记表 schema。");

        var targets = new Dictionary<string, ReleaseTarget>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.TryGetProperty("modules", out var modules))
        {
            foreach (var entry in modules.EnumerateArray())
                Add(targets, ReadModule(entry));
        }

        Add(targets, HostTarget());
        return targets.Values.OrderBy(target => target.Name, StringComparer.Ordinal).ToList();
    }

    public static ReleaseTarget Require(string registryPath, string name)
        => Load(registryPath).FirstOrDefault(target => target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? throw new InvalidOperationException($"{name} 不在发布登记表里。");

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
