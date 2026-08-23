using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class HostSnapshotBuilder
{
    public static string Build(string projectRoot, string version, string outputRoot, TextWriter log)
    {
        var solution = Path.Combine(projectRoot, "HistoryVulcan.sln");
        var project = Path.Combine(projectRoot, "b-Code-HistoryVulcan", "App", "App.csproj");
        var documentRoot = Path.Combine(projectRoot, "b-Office", "package");
        var catalogPath = Path.Combine(projectRoot, "b-Code-Eng", "release", "consumer-docs.json");
        var documentNames = ReadDocuments(catalogPath);
        var expectedFileVersion = version + ".0";

        var transaction = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Package." + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(transaction, "candidate");
        var hostDir = Path.Combine(staging, "host");
        var docsDir = Path.Combine(staging, "docs");
        var buildOutput = Path.Combine(transaction, "build");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(docsDir);

        try
        {
            ToolProcess.Run("dotnet", ["restore", solution, "--locked-mode", "--nologo", "-p:NuGetAudit=false"],
                projectRoot, log, "还原解决方案");
            ToolProcess.Run("dotnet", ["restore", project, "-r", "win-x64", "--locked-mode", "--nologo",
                    "-p:NuGetAudit=false", "-p:RestoreRecursive=false"],
                projectRoot, log, "还原宿主项目");
            ToolProcess.Run("dotnet", ["publish", project, "-c", "Release", "--no-restore",
                    "--self-contained", "false", "-r", "win-x64", "-o", hostDir,
                    "-p:BaseOutputPath=" + buildOutput + Path.DirectorySeparatorChar,
                    "-p:NuGetAudit=false"],
                projectRoot, log, "发布宿主");

            var exe = Path.Combine(hostDir, "HistoryVulcan.exe");
            if (!File.Exists(exe))
                throw new InvalidOperationException($"宿主快照缺少 HistoryVulcan.exe：{hostDir}");
            var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
            if (fileVersion != expectedFileVersion)
                throw new InvalidOperationException($"宿主可执行文件版本 {fileVersion} 与 {expectedFileVersion} 不一致。");

            foreach (var name in documentNames)
            {
                var source = Path.Combine(documentRoot, name);
                if (!File.Exists(source))
                    throw new InvalidOperationException($"缺少消费文档：{source}");
                File.Copy(source, Path.Combine(docsDir, name), overwrite: true);
            }

            var commit = ToolProcess.Capture("git", ["rev-parse", "HEAD"], projectRoot);
            var dirty = ToolProcess.Capture(
                "git",
                ["status", "--porcelain", "--", "b-Code-HistoryVulcan", "b-Code-Eng", "b-Office/package", "project.manifest.json"],
                projectRoot).Length > 0;

            var payload = SnapshotHashes.EnumeratePayload(staging);
            var prefix = Path.GetFullPath(staging).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            using (var stream = File.Create(Path.Combine(staging, "manifest.json")))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("product", "HistoryVulcan");
                writer.WriteString("version", version);
                writer.WriteString("channel", "current-host");
                writer.WriteString("runtime", "win-x64-framework-dependent");
                writer.WriteString("executable", "host/HistoryVulcan.exe");
                writer.WriteBoolean("selfContained", false);
                writer.WriteString("sourceCommit", commit);
                writer.WriteBoolean("sourceDirty", dirty);
                writer.WriteStartArray("documents");
                foreach (var name in documentNames)
                    writer.WriteStringValue(name);
                writer.WriteEndArray();
                writer.WriteStartArray("files");
                foreach (var file in payload)
                {
                    writer.WriteStartObject();
                    writer.WriteString("file", file.Substring(prefix.Length).Replace('\\', '/'));
                    writer.WriteNumber("bytes", new FileInfo(file).Length);
                    writer.WriteString("sha256", SnapshotHashes.Hash(file));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            SnapshotHashes.Write(staging);
            AssertSnapshot(staging, version, documentNames, expectedFileVersion);

            Directory.CreateDirectory(outputRoot);
            var incoming = Path.Combine(outputRoot, ".incoming-host-" + Guid.NewGuid().ToString("N"));
            SnapshotHashes.CopyDirectory(staging, incoming);
            PublishLayout.PromoteFlatHost(incoming, outputRoot, version);
            Directory.Delete(incoming, recursive: true);
            log.WriteLine($"已准备 HistoryVulcan {version} 宿主快照：{outputRoot}");
            return Path.GetFullPath(outputRoot);
        }
        finally
        {
            if (Directory.Exists(transaction))
                Directory.Delete(transaction, recursive: true);
        }
    }

    private static IReadOnlyList<string> ReadDocuments(string catalogPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(catalogPath, new UTF8Encoding(false)));
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new InvalidOperationException("消费文档清单无效。");
        var names = document.RootElement.GetProperty("documents")
            .EnumerateArray()
            .Select(item => item.GetProperty("file").GetString() ?? "")
            .Where(name => name.Length > 0)
            .ToList();
        if (names.Count == 0 || names.Count != names.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidOperationException("消费文档清单无效。");
        return names;
    }

    private static void AssertSnapshot(
        string root,
        string version,
        IReadOnlyList<string> documentNames,
        string expectedFileVersion)
    {
        var hostExe = Path.Combine(root, "host", "HistoryVulcan.exe");
        if (!File.Exists(hostExe))
            throw new InvalidOperationException("快照缺少 host/HistoryVulcan.exe。");
        var fileVersion = FileVersionInfo.GetVersionInfo(hostExe).FileVersion ?? "";
        if (fileVersion != expectedFileVersion)
            throw new InvalidOperationException($"宿主可执行文件版本 {fileVersion} 与 {expectedFileVersion} 不一致。");

        foreach (var name in documentNames)
        {
            if (!File.Exists(Path.Combine(root, "docs", name)))
                throw new InvalidOperationException($"快照缺少 docs/{name}");
        }

        foreach (var required in new[] { "manifest.json", SnapshotHashes.FileName })
        {
            if (!File.Exists(Path.Combine(root, required)))
                throw new InvalidOperationException($"快照缺少 {required}");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        if (manifest.RootElement.GetProperty("version").GetString() != version
            || manifest.RootElement.GetProperty("product").GetString() != "HistoryVulcan")
        {
            throw new InvalidOperationException("快照清单身份与请求的 HistoryVulcan 版本不一致。");
        }

        SnapshotHashes.Assert(root, skipHistory: true);
    }
}
