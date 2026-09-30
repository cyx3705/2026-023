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

/// <summary>
/// 发布目标的来源。
/// </summary>
/// <remarks>
/// 5.8.0（DEC-069）起宿主不再持有模块登记表：模块的发布描述写在<strong>模块仓自己的</strong>
/// <c>project.manifest.json</c> 的 <c>publish</c> 节里，模块名取同一文件的 <c>project.name</c>。
/// 新建或修改模块不需要碰宿主仓的任何文件；宿主这里只认识它自己。
///
/// 宿主冻结标签的期望值仍放在宿主仓 <c>b-Code-Eng/pipeline/host-freeze.json</c>——那是宿主自己的门，
/// 与模块无关。
/// </remarks>
internal static class ReleaseCatalog
{
    public const string HostName = "HistoryVulcan";
    public const string HostProjectDirectory = "2026-023-HistoryVulcan";
    public const string ProjectManifestFile = "project.manifest.json";
    public const string PublishSection = "publish";

    /// <summary>名字或项目目录是不是宿主自己。</summary>
    public static bool IsHost(string nameOrProject)
        => nameOrProject.Trim().Equals(HostName, StringComparison.OrdinalIgnoreCase)
           || nameOrProject.Trim().Equals(HostProjectDirectory, StringComparison.OrdinalIgnoreCase);

    public static ReleaseTarget Host() => new(
        HostName,
        "host",
        HostProjectDirectory,
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

    /// <summary>
    /// 读项目根下 <c>project.manifest.json</c> 的 <c>publish</c> 节。没有这一节返回 null；有但写坏了抛出。
    /// </summary>
    /// <param name="projectRoot">主树或工作区的根目录。</param>
    /// <param name="projectDirectory">主树在项目库里的目录名（工作区与主树共用）。</param>
    public static ReleaseTarget? TryLoadModule(string projectRoot, string projectDirectory)
    {
        var path = Path.Combine(projectRoot, ProjectManifestFile);
        if (!File.Exists(path))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty(PublishSection, out var publish) || publish.ValueKind != JsonValueKind.Object)
            return null;
        // 模板仓带着示范模块的描述（派生时随改名生成），但它自己不是模块。
        if (root.TryGetProperty("template", out var template)
            && template.TryGetProperty("isTemplate", out var isTemplate)
            && isTemplate.ValueKind == JsonValueKind.True)
            return null;

        var name = root.TryGetProperty("project", out var project) ? ReadString(project, "name") : "";
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"{path} 有 publish 节，但 project.name 为空。");
        if (IsHost(name))
            throw new InvalidOperationException($"{path}：宿主不走模块发布描述。");

        var target = new ReleaseTarget(
            name,
            "module",
            projectDirectory,
            ReadString(publish, "versionProps"),
            ReadString(publish, "versionProperty"),
            ReadString(publish, "sourceManifest"),
            ReadString(publish, "snapshotManifest", "module.manifest.json"),
            ReadString(publish, "identityProperty", "name"),
            ReadString(publish, "candidateDirectory", "z-Publish"),
            ReadString(publish, "formalDirectory", "z-Publish"),
            ReadString(publish, "packageDocuments", "b-Office\\package"),
            ReadPackage(publish),
            ReadValidation(publish),
            "");

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(target.VersionProps)) missing.Add("versionProps");
        if (string.IsNullOrWhiteSpace(target.VersionProperty)) missing.Add("versionProperty");
        if (string.IsNullOrWhiteSpace(target.SourceManifest)) missing.Add("sourceManifest");
        if (string.IsNullOrWhiteSpace(target.Package?.Project)) missing.Add("package.project");
        if (missing.Count > 0)
            throw new InvalidOperationException($"{path} 的 publish 节缺少：{string.Join("、", missing)}。");
        return target;
    }

    /// <summary>
    /// 在项目库根下找出所有自带 publish 节的项目。这是发现，不是登记：宿主不保存结果。
    /// 写坏的描述不拦住别的项目，放进 <paramref name="errors"/>。
    /// </summary>
    public static IReadOnlyList<ReleaseTarget> Discover(string libraryRoot, List<string>? errors = null)
    {
        var targets = new List<ReleaseTarget>();
        if (!Directory.Exists(libraryRoot))
            return targets;

        foreach (var directory in Directory.GetDirectories(libraryRoot).OrderBy(path => path, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (IsHost(name))
                continue;
            try
            {
                if (TryLoadModule(directory, name) is { } target)
                    targets.Add(target);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
            {
                errors?.Add($"{name}: {ex.Message}");
            }
        }

        var duplicate = targets.GroupBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            errors?.Add($"模块名 {duplicate.Key} 同时出现在：{string.Join("、", duplicate.Select(target => target.ProjectDirectory))}");
        return targets;
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
