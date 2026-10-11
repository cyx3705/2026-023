using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace HistoryVulcan.Services.Modules;

/// <summary>
/// 模块包就是一个 nupkg（7.0.0）。发布归档、本机安装、宿主装载读的是同一个文件。
/// 身份在 nuspec 的 id / version；原始模块版本（含 v 前缀）与 <c>ui</c>、<c>dependsOn</c>、
/// <c>pinned</c> 在包内的 <c>module.manifest.json</c>。程序集由宿主扫描，不再点名
/// <c>artifact</c> / <c>deps</c>。内容以包哈希发布完整只读快照，同包跨进程复用。
/// </summary>
internal static class ModuleNupkg
{
    internal const string Extension = ".nupkg";
    internal const string ManifestFileName = "module.manifest.json";

    internal static string RuntimeFileName(string moduleName) => moduleName + Extension;

    internal static bool TryResolveFile(string path, out string nupkg, out string error)
    {
        nupkg = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            error = "安装路径必须是绝对的 NuGet 包。";
            return false;
        }

        var full = Path.GetFullPath(path.Trim());
        if (File.Exists(full) && full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            nupkg = full;
            return true;
        }

        if (Directory.Exists(full))
        {
            var found = Directory.GetFiles(full, "*" + Extension, SearchOption.TopDirectoryOnly)
                .Where(file => !RuntimeModuleDiscoverySource.IsTransientPackageDirectory(Path.GetFileName(file)))
                .ToArray();
            if (found.Length == 1)
            {
                nupkg = Path.GetFullPath(found[0]);
                return true;
            }

            error = found.Length == 0
                ? $"目录里没有 nupkg: {full}"
                : $"目录里有多个 nupkg: {full}";
            return false;
        }

        error = $"不是 NuGet 包: {full}";
        return false;
    }

    internal static bool TryRead(
        string nupkgPath,
        out ModuleDiscoveryEntry entry,
        out string code,
        out string error)
    {
        entry = null!;
        code = "";
        error = "";
        if (string.IsNullOrWhiteSpace(nupkgPath) || !Path.IsPathFullyQualified(nupkgPath))
        {
            code = "invalid-package";
            error = "NuGet 包路径必须是绝对路径。";
            return false;
        }

        var full = Path.GetFullPath(nupkgPath.Trim());
        if (!File.Exists(full) || !full.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            code = "invalid-package";
            error = $"不是 NuGet 包: {full}";
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(full);
            if (!TryReadNuspec(archive, out var name, out var version, out error))
            {
                code = "invalid-package";
                return false;
            }

            if (!RuntimeModulePackageStore.IsSafeModuleName(name))
            {
                code = "invalid-manifest";
                error = $"模块名不能安全映射为包文件名: {name}";
                return false;
            }

            var manifest = archive.Entries.FirstOrDefault(item =>
                EntryName(item).Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase));
            if (manifest == null)
            {
                code = "invalid-manifest";
                error = $"包内没有 {ManifestFileName}。";
                return false;
            }

            using var reader = new StreamReader(manifest.Open(), Encoding.UTF8);
            var json = reader.ReadToEnd();
            if (!TryReadManifest(json, name, version, out var moduleVersion, out var ui, out var pinned, out var frontend, out var dependsOn, out error))
            {
                code = "invalid-manifest";
                return false;
            }

            entry = new ModuleDiscoveryEntry(
                name,
                moduleVersion,
                full,
                full,
                full,
                ui,
                null,
                [])
            {
                Pinned = pinned,
                FrontendRole = frontend,
                DependsOn = dependsOn,
            };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            code = "invalid-package";
            error = ex.Message;
            return false;
        }
    }

    internal static IReadOnlyDictionary<string, byte[]> ReadAssemblies(string nupkgPath)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using var archive = ZipFile.OpenRead(nupkgPath);
        foreach (var entry in archive.Entries)
        {
            var name = EntryName(entry);
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || IsInfrastructure(name))
                continue;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            try
            {
                using var image = new System.Reflection.PortableExecutable.PEReader(
                    buffer, System.Reflection.PortableExecutable.PEStreamOptions.LeaveOpen);
                if (!image.HasMetadata)
                    continue;
            }
            catch (BadImageFormatException)
            {
                // 随包的原生 DLL 或其它二进制内容只展开，不交给托管程序集装载器。
                continue;
            }

            result[Path.GetFileNameWithoutExtension(name.Replace('\\', '/'))] = buffer.ToArray();
        }

        return result;
    }

    /// <summary>打开包后的只读内容根。安装单元仍是 nupkg，这个目录不参与发现。</summary>
    internal static string ExtractContent(string nupkgPath, string moduleName)
    {
        using var package = File.OpenRead(nupkgPath);
        var hash = Convert.ToHexString(SHA256.HashData(package));
        package.Position = 0;
        var contentRoot = ContentRootDirectory(nupkgPath, moduleName);
        var content = Path.Combine(contentRoot, hash);
        if (Directory.Exists(content))
            return content;

        Directory.CreateDirectory(contentRoot);
        var staging = Path.Combine(contentRoot, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var name = EntryName(entry);
                if (name.Length == 0 || name.EndsWith('/') || IsInfrastructure(name))
                    continue;

                var relative = name.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"包内容路径越界: {name}");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }

            try
            {
                Directory.Move(staging, content);
            }
            catch (IOException) when (Directory.Exists(content))
            {
                // 另一个宿主/离线 CLI 已原子发布同一哈希的完整内容，复用它。
            }

            return content;
        }
        finally
        {
            // 只清理本次创建的中转目录，任何已有内容快照都可能仍被活模块读取。
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    internal static string ContentDirectory(string nupkgPath, string moduleName)
        => Path.Combine(ContentRootDirectory(nupkgPath, moduleName), Hash(nupkgPath));

    private static string ContentRootDirectory(string nupkgPath, string moduleName)
    {
        var packageDirectory = Path.GetDirectoryName(Path.GetFullPath(nupkgPath))
            ?? throw new InvalidOperationException("NuGet 包没有所在目录。");
        var parent = Directory.GetParent(packageDirectory)?.FullName ?? packageDirectory;
        return Path.Combine(parent, "ModuleContent", moduleName);
    }

    internal static bool SameContent(string first, string second)
        => Hash(first).Equals(Hash(second), StringComparison.OrdinalIgnoreCase);

    internal static string Hash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static void Pack(
        string nupkgPath,
        string name,
        string version,
        IEnumerable<(string Relative, string SourcePath)> files)
    {
        var payload = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relative, source) in files)
        {
            var key = relative.Replace('\\', '/').TrimStart('/');
            if (key.Length == 0 || IsInfrastructure(key))
                continue;
            payload[key] = File.ReadAllBytes(source);
        }

        PackBytes(nupkgPath, name, version, payload);
    }

    internal static void PackBytes(
        string nupkgPath,
        string name,
        string version,
        IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(nupkgPath))!);
        if (File.Exists(nupkgPath))
            File.Delete(nupkgPath);

        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        WriteEntry(archive, name + ".nuspec", Encoding.UTF8.GetBytes(Nuspec(name, version)));
        foreach (var (relative, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var key = relative.Replace('\\', '/').TrimStart('/');
            if (key.Equals(name + ".nuspec", StringComparison.OrdinalIgnoreCase) || IsInfrastructure(key))
                continue;
            WriteEntry(archive, key, key.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase)
                ? PreserveModuleVersion(bytes, version)
                : bytes);
        }
    }

    private static byte[] PreserveModuleVersion(byte[] manifest, string version)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.TryGetProperty("version", out _))
                return manifest;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                    property.WriteTo(writer);
                writer.WriteString("version", version);
                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }
        catch (JsonException)
        {
            // 校验负责拒绝坏清单；打包原样保留它，避免把坏输入悄悄修成另一份内容。
            return manifest;
        }
    }

    internal static void RewriteManifest(string nupkgPath, Func<string, string> edit)
    {
        var temp = nupkgPath + ".rewrite";
        if (File.Exists(temp))
            File.Delete(temp);

        using (var source = ZipFile.OpenRead(nupkgPath))
        using (var destination = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            foreach (var entry in source.Entries)
            {
                var name = EntryName(entry);
                if (name.Length == 0 || name.EndsWith('/'))
                    continue;
                byte[] bytes;
                using (var stream = entry.Open())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    bytes = buffer.ToArray();
                }

                if (name.Equals(ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    bytes = Encoding.UTF8.GetBytes(edit(Encoding.UTF8.GetString(bytes)));
                WriteEntry(destination, name, bytes);
            }
        }

        File.Delete(nupkgPath);
        File.Move(temp, nupkgPath);
    }

    private static bool TryReadNuspec(ZipArchive archive, out string name, out string version, out string error)
    {
        name = "";
        version = "";
        error = "";
        var nuspec = archive.Entries.FirstOrDefault(entry =>
        {
            var name = EntryName(entry);
            return name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !IsInfrastructure(name);
        });
        if (nuspec == null)
        {
            error = "包内没有 nuspec。";
            return false;
        }

        using var stream = nuspec.Open();
        XDocument document;
        try
        {
            document = XDocument.Load(stream);
        }
        catch (System.Xml.XmlException ex)
        {
            error = ex.Message;
            return false;
        }
        name = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim() ?? "";
        version = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "version")?.Value.Trim() ?? "";
        if (name.Length == 0 || version.Length == 0)
        {
            error = "nuspec 缺少 id 或 version。";
            return false;
        }

        return true;
    }

    private static bool TryReadManifest(
        string json,
        string packageName,
        string packageVersion,
        out string moduleVersion,
        out bool ui,
        out bool pinned,
        out bool frontend,
        out IReadOnlyList<string> dependsOn,
        out string error)
    {
        moduleVersion = packageVersion;
        ui = false;
        pinned = false;
        frontend = false;
        dependsOn = [];
        error = "";
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "module.manifest.json 必须是 JSON 对象。";
                return false;
            }

            if (!root.TryGetProperty("schemaVersion", out var schema)
                || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1)
            {
                error = "schemaVersion 必须为 1。";
                return false;
            }

            if (!root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), ModuleManifestReader.ManifestType, StringComparison.Ordinal))
            {
                error = $"type 必须为 {ModuleManifestReader.ManifestType}。";
                return false;
            }

            if (!root.TryGetProperty("ui", out var uiValue) || uiValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "ui 必须是布尔值。";
                return false;
            }

            ui = uiValue.GetBoolean();
            if (root.TryGetProperty("role", out var role))
            {
                if (role.ValueKind != JsonValueKind.String || role.GetString() != "frontend" || !ui)
                {
                    error = "role 只接受 frontend，且前端必须声明 ui=true。";
                    return false;
                }
                frontend = true;
            }
            pinned = root.TryGetProperty("pinned", out var pinnedValue) && pinnedValue.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.Equals(name.GetString()?.Trim(), packageName, StringComparison.OrdinalIgnoreCase))
            {
                error = "module.manifest.json 的 name 与包 id 不一致。";
                return false;
            }

            if (root.TryGetProperty("version", out var version))
            {
                if (version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString()))
                {
                    error = "module.manifest.json 的 version 必须是非空字符串。";
                    return false;
                }

                var rawVersion = version.GetString()!;
                if (!string.Equals(NuGetVersionOf(rawVersion), NuGetVersionOf(packageVersion), StringComparison.Ordinal))
                {
                    error = "module.manifest.json 的 version 与包版本不一致。";
                    return false;
                }

                moduleVersion = rawVersion;
            }

            dependsOn = ModuleHost.ReadDependsOnJson(json);
            return true;
        }
    }

    internal static string NuGetVersionOf(string version)
        => version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version[1..] : version;

    private static string Nuspec(string name, string version)
        => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{Escape(name)}</id>
                <version>{Escape(NuGetVersionOf(version))}</version>
                <authors>HistoryVulcan</authors>
                <description>{Escape(name)}</description>
              </metadata>
            </package>
            """;

    private static string Escape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name.Replace('\\', '/'), CompressionLevel.Optimal);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static string EntryName(ZipArchiveEntry entry)
        => entry.FullName.Replace('\\', '/');

    private static bool IsInfrastructure(string name)
    {
        var normalized = name.Replace('\\', '/');
        return normalized.StartsWith("_rels/", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("package/", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
    }
}
