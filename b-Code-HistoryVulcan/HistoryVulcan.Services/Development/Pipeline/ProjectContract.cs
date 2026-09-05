using System.Text.Json;
using System.Text.RegularExpressions;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class ProjectContract
{
    public static void Validate(string projectRoot, string kind, bool instantiation, string? registryPath)
    {
        var errors = new List<string>();
        var manifestPath = Path.Combine(projectRoot, "project.manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException("缺少 project.manifest.json。");
        }

        if (HasUtf8Bom(manifestPath))
            errors.Add("project.manifest.json 必须是无 BOM 的 UTF-8。");

        JsonElement manifest;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            manifest = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("project.manifest.json 不是合法 JSON：" + ex.Message);
        }

        var contract = manifest.TryGetProperty("contract", out var contractElement)
            ? contractElement
            : default;

        CheckRequiredFiles(projectRoot, contract, errors);
        CheckManifestShape(manifest, contract, instantiation, errors);
        CheckPaths(projectRoot, manifest, errors);
        CheckDocuments(projectRoot, manifest, errors);
        CheckMarkdownLinks(projectRoot, manifest, contract, errors);
        CheckRequirements(projectRoot, manifest, contract, errors);
        CheckInstantiation(projectRoot, manifest, instantiation, errors);
        if (kind.Equals("host", StringComparison.OrdinalIgnoreCase))
            CheckHost(projectRoot, manifest, registryPath, errors);

        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"[{kind}] 项目合同校验失败，共 {errors.Count} 项：\n  - " + string.Join("\n  - ", errors));
    }

    private static void CheckRequiredFiles(string root, JsonElement contract, List<string> errors)
    {
        var required = ReadStringArray(contract, "requiredFiles",
            ["AGENTS.md", "README.md", ".ignore", "project.manifest.json"]);
        foreach (var relative in required)
        {
            var full = ResolveInside(root, relative, "必需文件", errors);
            if (full != null && !File.Exists(full))
                errors.Add($"缺少必需文件：{relative}");
        }
    }

    private static void CheckManifestShape(JsonElement manifest, JsonElement contract, bool instantiation, List<string> errors)
    {
        if (manifest.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number)
        {
            if (schema.GetInt32() != 1)
                errors.Add($"不支持的 schemaVersion：{schema.GetInt32()}");
        }
        else
        {
            errors.Add("清单缺少属性 'schemaVersion'。");
        }

        foreach (var name in new[] { "template", "project", "paths", "documents", "commands", "contextExclusions" })
        {
            if (!manifest.TryGetProperty(name, out _))
                errors.Add($"清单缺少属性 '{name}'。");
        }

        if (manifest.TryGetProperty("template", out var template)
            && template.TryGetProperty("isTemplate", out var isTemplate))
        {
            if (isTemplate.ValueKind != JsonValueKind.True && isTemplate.ValueKind != JsonValueKind.False)
                errors.Add("template.isTemplate 必须是布尔值。");
            else if (instantiation && isTemplate.GetBoolean())
                errors.Add("实例化校验要求 template.isTemplate 为 false。");
        }

        if (manifest.TryGetProperty("project", out var project))
        {
            var requiredFields = ReadStringArray(contract, "requiredProjectFields",
                ["id", "name", "title", "kind", "status"]);
            foreach (var field in requiredFields)
            {
                if (!project.TryGetProperty(field, out var value) || IsEmpty(value))
                    errors.Add($"project.{field} 不能为空。");
            }
        }

        if (manifest.TryGetProperty("commands", out var commands))
        {
            foreach (var name in new[] { "setup", "build", "test", "verify", "run", "package" })
            {
                if (!commands.TryGetProperty(name, out var value))
                    errors.Add($"commands 缺少属性 '{name}'。");
                else if (value.ValueKind != JsonValueKind.Null
                         && (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
                    errors.Add($"commands.{name} 必须是非空字符串或 null。");
            }
        }
    }

    private static void CheckPaths(string root, JsonElement manifest, List<string> errors)
    {
        if (!manifest.TryGetProperty("paths", out var paths))
            return;

        foreach (var name in new[] { "activeRoots", "sourceRoots", "generatedRoots", "archiveRoots", "allowedRootDirectoryPrefixes" })
        {
            if (!paths.TryGetProperty(name, out _))
                errors.Add($"paths 缺少属性 '{name}'。");
        }

        foreach (var relative in ReadStringArray(paths, "activeRoots").Concat(ReadStringArray(paths, "sourceRoots")))
        {
            var full = ResolveInside(root, relative, "声明目录", errors);
            if (full != null && !Directory.Exists(full))
                errors.Add($"声明的目录不存在：{relative}");
        }

        var prefixes = ReadStringArray(paths, "allowedRootDirectoryPrefixes");
        foreach (var expected in new[] { "a-", "b-", "z-" })
        {
            if (!prefixes.Contains(expected, StringComparer.Ordinal))
                errors.Add($"paths.allowedRootDirectoryPrefixes 必须包含 '{expected}'。");
        }

        var allowedNames = ReadStringArray(paths, "allowedRootDirectoryNames");
        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.'))
                continue;
            var allowed = prefixes.Any(prefix =>
                    !string.IsNullOrWhiteSpace(prefix)
                    && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                || allowedNames.Any(allowedName =>
                    name.Equals(allowedName, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
                errors.Add($"根目录不符合命名合同：{name}");
        }
    }

    private static void CheckDocuments(string root, JsonElement manifest, List<string> errors)
    {
        if (!manifest.TryGetProperty("documents", out var documents))
            return;
        foreach (var property in documents.EnumerateObject())
        {
            var relative = property.Value.GetString() ?? "";
            var full = ResolveInside(root, relative, $"documents.{property.Name}", errors);
            if (full != null && !File.Exists(full))
                errors.Add($"声明的文档不存在：{relative}");
        }
    }

    private static void CheckMarkdownLinks(string root, JsonElement manifest, JsonElement contract, List<string> errors)
    {
        var mode = ReadString(contract, "linkCheckMode", "recursive");
        IEnumerable<string> files;
        if (mode.Equals("declared", StringComparison.OrdinalIgnoreCase))
        {
            var declared = new List<string> { Path.Combine(root, "README.md") };
            if (manifest.TryGetProperty("documents", out var documents))
            {
                foreach (var property in documents.EnumerateObject())
                {
                    var relative = property.Value.GetString() ?? "";
                    if (relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        declared.Add(Path.Combine(root, relative));
                }
            }

            foreach (var extra in ReadStringArray(contract, "linkCheckAdditionalFiles"))
                declared.Add(Path.Combine(root, extra));
            files = declared.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists);
        }
        else
        {
            var exclusions = ReadStringArray(contract, "linkCheckExclusions", [@"*\history\*", @"*-References\*"]);
            files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                .Where(path => !exclusions.Any(pattern => MatchesLike(path, pattern)));
        }

        var link = new Regex(@"\[[^\]]+\]\((?<target>[^)]+)\)");
        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            if (string.IsNullOrEmpty(content))
                continue;
            foreach (Match match in link.Matches(content))
            {
                var target = match.Groups["target"].Value.Trim().Trim('<', '>');
                if (target.StartsWith('#') || Regex.IsMatch(target, @"^[a-zA-Z][a-zA-Z0-9+.-]*:"))
                    continue;
                var pathPart = target.Split('#', 2)[0];
                if (string.IsNullOrWhiteSpace(pathPart))
                    continue;
                try
                {
                    pathPart = Uri.UnescapeDataString(pathPart);
                }
                catch (Exception)
                {
                    errors.Add($"Markdown 链接无法解码：{Relative(root, file)} -> {target}");
                    continue;
                }

                var linked = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, pathPart));
                var repoRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!linked.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                    && !linked.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Path.Exists(linked))
                    errors.Add($"失效的 Markdown 链接：{Relative(root, file)} -> {target}");
            }
        }
    }

    internal static void CheckRequirements(string root, JsonElement manifest, JsonElement contract, List<string> errors)
    {
        var testRoot = ReadString(contract, "requirementTestRoot");
        if (string.IsNullOrWhiteSpace(testRoot))
            return;
        var tests = ResolveInside(root, testRoot, "contract.requirementTestRoot", errors);
        if (tests == null || !manifest.TryGetProperty("documents", out var documents))
            return;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var heading = new Regex(@"(?m)^#{2,6}\s+(?<id>REQ-[A-Z0-9-]+)\b[^\r\n]*");
        var acceptance = new Regex(@"验收：\s*`(?<type>[A-Za-z0-9_]+)\.(?<method>[A-Za-z0-9_]+)`");
        foreach (var document in documents.EnumerateObject())
        {
            var relative = document.Value.GetString() ?? "";
            if (!relative.Replace('\\', '/').StartsWith("b-Office/current/", StringComparison.Ordinal))
                continue;
            var path = ResolveInside(root, relative, "现行需求文档", errors);
            if (path == null || !File.Exists(path))
                continue;
            var content = File.ReadAllText(path);
            var headings = heading.Matches(content);
            for (var i = 0; i < headings.Count; i++)
            {
                var item = headings[i];
                var id = item.Groups["id"].Value;
                if (!ids.Add(id))
                    errors.Add($"重复需求编号：{id} ({relative})");
                var end = i + 1 < headings.Count ? headings[i + 1].Index : content.Length;
                var references = acceptance.Matches(content[item.Index..end]);
                if (references.Count == 0)
                    errors.Add($"需求缺少本仓验收测试：{id}");
                foreach (Match reference in references)
                {
                    var type = reference.Groups["type"].Value;
                    var method = reference.Groups["method"].Value;
                    var source = Path.Combine(tests, type + ".cs");
                    var signature = @"\[(?:Fact|Theory)(?:\([^\]]*\))?\][\s\S]*?public\s+(?:async\s+)?(?:void|Task)\s+"
                        + Regex.Escape(method) + @"\s*\(";
                    if (!File.Exists(source) || !Regex.IsMatch(File.ReadAllText(source), signature))
                        errors.Add($"验收测试不存在：{id} -> {type}.{method}");
                }
            }
        }
        if (ids.Count == 0)
            errors.Add("未找到现行需求编号。");
    }

    private static void CheckInstantiation(string root, JsonElement manifest, bool instantiation, List<string> errors)
    {
        var isTemplate = true;
        if (manifest.TryGetProperty("template", out var template)
            && template.TryGetProperty("isTemplate", out var flag)
            && (flag.ValueKind == JsonValueKind.True || flag.ValueKind == JsonValueKind.False))
        {
            isTemplate = flag.GetBoolean();
        }

        if (!(instantiation || !isTemplate))
            return;

        if (manifest.TryGetProperty("project", out var project))
        {
            if (ReadString(project, "id") == "0000-001")
                errors.Add("实例化项目必须替换模板 project.id。");
            if (ReadString(project, "name") == "AIReady")
                errors.Add("实例化项目必须替换模板 project.name。");
            if (ReadString(project, "status") == "template")
                errors.Add("实例化项目必须替换模板 project.status。");
        }

        var readmePath = Path.Combine(root, "README.md");
        if (File.Exists(readmePath) && Regex.IsMatch(File.ReadAllText(readmePath), @"(?m)^# OneHistory AI-Ready"))
            errors.Add("实例化项目必须替换模板根 README。");

        var currentDocuments = new List<string>();
        if (manifest.TryGetProperty("documents", out var documents))
        {
            foreach (var property in documents.EnumerateObject())
            {
                var relative = property.Value.GetString() ?? "";
                if (Regex.IsMatch(relative, @"(^|/)current/"))
                    currentDocuments.Add(relative);
            }
        }

        var placeholder = new Regex(@"\{\{[^{}\r\n]+\}\}");
        foreach (var relative in new[] { "README.md", "project.manifest.json" }.Concat(currentDocuments).Distinct())
        {
            var full = Path.Combine(root, relative);
            if (File.Exists(full) && placeholder.IsMatch(File.ReadAllText(full)))
                errors.Add($"实例化占位符仍留在：{relative}");
        }
    }

    private static void CheckHost(string root, JsonElement manifest, string? registryPath, List<string> errors)
    {
        if (!manifest.TryGetProperty("contract", out var contract)
            || !contract.TryGetProperty("host", out var host))
        {
            errors.Add("宿主项目必须在 project.manifest.json 声明 contract.host。");
            return;
        }

        if (host.TryGetProperty("identity", out var identity) && manifest.TryGetProperty("project", out var project))
        {
            var expectedId = ReadString(identity, "id");
            var expectedName = ReadString(identity, "name");
            if (ReadString(project, "id") != expectedId || ReadString(project, "name") != expectedName)
                errors.Add($"项目身份必须是 {expectedId}/{expectedName}。");
        }

        if (manifest.TryGetProperty("project", out var projectElement) && !string.IsNullOrWhiteSpace(registryPath))
        {
            var projectName = ReadString(projectElement, "name");
            var actualFreeze = ReadString(projectElement, "freezeTag");
            if (File.Exists(registryPath))
            {
                using var registry = JsonDocument.Parse(File.ReadAllText(registryPath));
                if (registry.RootElement.TryGetProperty("hosts", out var hosts))
                {
                    var match = hosts.EnumerateArray()
                        .FirstOrDefault(item => ReadString(item, "name") == projectName);
                    if (match.ValueKind == JsonValueKind.Undefined)
                        errors.Add($"发布登记表没有 {projectName} 的宿主条目。");
                    else
                    {
                        var expectedFreeze = ReadString(match, "freezeTag");
                        if (actualFreeze != expectedFreeze)
                            errors.Add($"冻结标签必须保持 {expectedFreeze}（发现 {actualFreeze}）。");
                    }
                }
            }
            else
            {
                errors.Add($"找不到发布登记表，无法核对冻结标签：{registryPath}");
            }
        }

        var versionProps = ReadString(host, "versionProps");
        if (!string.IsNullOrWhiteSpace(versionProps) && manifest.TryGetProperty("project", out var projectForVersion))
        {
            var full = ResolveInside(root, versionProps, "contract.host.versionProps", errors);
            var property = ReadString(host, "versionProperty");
            if (string.IsNullOrWhiteSpace(property))
                errors.Add("声明了 versionProps 时必须同时声明 contract.host.versionProperty。");
            else if (full != null)
            {
                if (!File.Exists(full))
                    errors.Add($"版本源缺失：{versionProps}");
                else
                {
                    var text = File.ReadAllText(full);
                    var match = Regex.Match(text, $"<{Regex.Escape(property)}>(?<v>[^<]+)</{Regex.Escape(property)}>");
                    var source = match.Success ? match.Groups["v"].Value : "";
                    var manifestVersion = ReadString(projectForVersion, "version");
                    if (source != manifestVersion)
                        errors.Add($"project.version ({manifestVersion}) 必须与 {versionProps}/{property} ({source}) 一致。");
                }
            }
        }
    }

    private static bool HasUtf8Bom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }

    private static string? ResolveInside(string root, string relative, string context, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            errors.Add($"{context} 必须是非空的仓库相对路径：{relative}");
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{context} 指向仓库之外：{relative}");
            return null;
        }

        return full;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name, string[]? fallback = null)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return fallback ?? [];
        }

        return array.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList();
    }

    private static string ReadString(JsonElement element, string name, string fallback = "")
        => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? fallback
            : element.TryGetProperty(name, out var value) ? value.GetString() ?? fallback : fallback;

    private static bool IsEmpty(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() == 0,
        JsonValueKind.Null => true,
        JsonValueKind.Undefined => true,
        _ => false,
    };

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool MatchesLike(string path, string pattern)
        => path.Contains("history", StringComparison.OrdinalIgnoreCase)
           && pattern.Contains("history", StringComparison.OrdinalIgnoreCase)
           || path.Contains("-References", StringComparison.OrdinalIgnoreCase)
           && pattern.Contains("-References", StringComparison.OrdinalIgnoreCase);
}
