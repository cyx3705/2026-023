using System.Diagnostics;
using System.Text.Json;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class HostSnapshotBuilder
{
    public static string Build(string projectRoot, string version, string outputRoot, TextWriter log)
    {
        var solution = Path.Combine(projectRoot, "HistoryVulcan.sln");
        var project = Path.Combine(projectRoot, "b-Code-HistoryVulcan", "App", "App.csproj");
        var cliProject = Path.Combine(projectRoot, "b-Code-HistoryVulcan", "Cli", "HistoryVulcan.Cli.csproj");
        // 6.1.0（DEC-072）：快照不再带 docs/。模块开发手册是宿主仓的现行文档，
        // 指令说明书由 Diana 现查指令目录，两者都不随包分发。
        var expectedFileVersion = version + ".0";

        var transaction = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Package." + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(transaction, "candidate");
        var hostDir = Path.Combine(staging, "host");
        var buildOutput = Path.Combine(transaction, "build");
        Directory.CreateDirectory(hostDir);

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
            ToolProcess.Run("dotnet", ["publish", cliProject, "-c", "Release", "--no-restore",
                    "--self-contained", "false", "-r", "win-x64", "-o", hostDir,
                    "-p:BaseOutputPath=" + buildOutput + Path.DirectorySeparatorChar,
                    "-p:NuGetAudit=false"],
                projectRoot, log, "发布 Console CLI");

            var exe = Path.Combine(hostDir, "HistoryVulcan.exe");
            if (!File.Exists(exe))
                throw new InvalidOperationException($"宿主快照缺少 HistoryVulcan.exe：{hostDir}");
            if (!File.Exists(Path.Combine(hostDir, "HistoryVulcan.Cli.exe")))
                throw new InvalidOperationException($"宿主快照缺少 HistoryVulcan.Cli.exe：{hostDir}");
            var fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "";
            if (fileVersion != expectedFileVersion)
                throw new InvalidOperationException($"宿主可执行文件版本 {fileVersion} 与 {expectedFileVersion} 不一致。");

            var commit = ToolProcess.Capture("git", ["rev-parse", "HEAD"], projectRoot);
            var dirty = ToolProcess.Capture(
                "git",
                ["status", "--porcelain", "--", "b-Code-HistoryVulcan", "b-Code-Eng", "project.manifest.json"],
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
            AssertSnapshot(staging, version, expectedFileVersion);

            Directory.CreateDirectory(outputRoot);
            var incoming = Path.Combine(outputRoot, ".incoming-host-" + Guid.NewGuid().ToString("N"));
            try
            {
                SnapshotHashes.CopyDirectory(staging, incoming);
                PublishLayout.PromoteFlatHost(incoming, outputRoot, version, log);
            }
            finally
            {
                // 中转目录必须建在发布根里（同卷才能用 Move 促级），失败时不清理就留在
                // z 快照里；而 z-* 是纳入 git 的正式快照，泄漏的中转目录会被一起提交。
                // 5.1.0 审查时 z-Publish 下已有三个 .incoming-host-* 进了版本库，
                // 每个带一份 745K 的宿主副本。promote 成功后此目录已空，失败时它是整包。
                if (Directory.Exists(incoming))
                    Directory.Delete(incoming, recursive: true);
            }

            log.WriteLine($"已准备 HistoryVulcan {version} 宿主快照：{outputRoot}");
            return Path.GetFullPath(outputRoot);
        }
        finally
        {
            if (Directory.Exists(transaction))
                Directory.Delete(transaction, recursive: true);
        }
    }

    private static void AssertSnapshot(
        string root,
        string version,
        string expectedFileVersion)
    {
        var hostExe = Path.Combine(root, "host", "HistoryVulcan.exe");
        if (!File.Exists(hostExe))
            throw new InvalidOperationException("快照缺少 host/HistoryVulcan.exe。");
        if (!File.Exists(Path.Combine(root, "host", "HistoryVulcan.Cli.exe")))
            throw new InvalidOperationException("快照缺少 host/HistoryVulcan.Cli.exe。");
        var fileVersion = FileVersionInfo.GetVersionInfo(hostExe).FileVersion ?? "";
        if (fileVersion != expectedFileVersion)
            throw new InvalidOperationException($"宿主可执行文件版本 {fileVersion} 与 {expectedFileVersion} 不一致。");

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
