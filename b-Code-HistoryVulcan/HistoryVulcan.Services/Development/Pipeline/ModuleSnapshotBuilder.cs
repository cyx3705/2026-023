using System.Text;
using System.Text.Json;

namespace HistoryVulcan.Services.Development.Pipeline;

internal static class ModuleSnapshotBuilder
{
    public static string Build(
        string projectRoot,
        ReleaseTarget target,
        string version,
        string stagingRoot,
        string hostSnapshotRoot,
        TextWriter log)
    {
        if (target.Package is null)
            throw new InvalidOperationException($"{target.Name} 未声明 package（project/outputDirectory/files）。");

        var sourceManifest = Path.Combine(projectRoot, target.SourceManifest);
        if (!File.Exists(sourceManifest))
            throw new InvalidOperationException($"缺少源模块清单：{sourceManifest}");
        using (var document = JsonDocument.Parse(File.ReadAllText(sourceManifest, new UTF8Encoding(false))))
        {
            var name = document.RootElement.GetProperty("name").GetString();
            var manifestVersion = document.RootElement.GetProperty("version").GetString();
            if (name != target.Name || manifestVersion != version)
                throw new InvalidOperationException($"源清单必须在发布前与 {target.Name} {version} 对齐。");
        }

        Environment.SetEnvironmentVariable("HISTORYVULCAN_PACKAGE_ROOT", hostSnapshotRoot);
        var project = Path.Combine(projectRoot, target.Package.Project);
        ToolProcess.Run(
            "dotnet",
            ["build", project, "-c", "Release", "--nologo", "-p:NuGetAudit=false",
                "-p:HistoryVulcanPackageRoot=" + hostSnapshotRoot],
            projectRoot,
            log,
            "构建模块候选包");

        var output = Path.Combine(projectRoot, target.Package.OutputDirectory);
        if (!Directory.Exists(output))
            throw new InvalidOperationException($"构建输出不存在：{output}");

        Directory.CreateDirectory(stagingRoot);
        foreach (var name in target.Package.Files)
        {
            var source = Path.Combine(output, name);
            if (!File.Exists(source))
                throw new InvalidOperationException($"缺少预期产物：{source}");
            var destination = Path.Combine(stagingRoot, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }

        var docsRoot = Path.Combine(stagingRoot, "docs");
        var documentSource = Path.Combine(projectRoot, target.PackageDocuments);
        if (!Directory.Exists(documentSource))
            throw new InvalidOperationException($"包文档源缺失：{documentSource}");
        var documents = Directory.GetFiles(documentSource, "*.md");
        if (documents.Length == 0)
            throw new InvalidOperationException($"没有消费 Markdown 文档：{documentSource}");
        Directory.CreateDirectory(docsRoot);
        foreach (var document in documents)
            File.Copy(document, Path.Combine(docsRoot, Path.GetFileName(document)), overwrite: true);

        SnapshotHashes.Write(stagingRoot);
        AssertSnapshot(stagingRoot, target, version);
        return stagingRoot;
    }

    public static void AssertSnapshot(string root, ReleaseTarget target, string version)
    {
        var manifestName = string.IsNullOrWhiteSpace(target.SnapshotManifest)
            ? "module.manifest.json"
            : target.SnapshotManifest;
        var manifestPath = Path.Combine(root, manifestName);
        if (!File.Exists(manifestPath) || !File.Exists(Path.Combine(root, SnapshotHashes.FileName)))
            throw new InvalidOperationException($"模块快照不完整：{root}");

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, new UTF8Encoding(false)));
        var identity = document.RootElement.GetProperty(target.IdentityProperty).GetString();
        var manifestVersion = document.RootElement.GetProperty("version").GetString();
        if (identity != target.Name || manifestVersion != version)
            throw new InvalidOperationException($"模块快照身份不匹配：期望 {target.Name} {version}");

        SnapshotHashes.Assert(root, skipHistory: true);
    }

    public static void RunValidation(
        ReleaseTarget target,
        string projectRoot,
        string candidateRoot,
        TextWriter log)
    {
        foreach (var step in target.Validation)
        {
            if (!step.Tool.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && !step.Tool.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{target.Name} 的验证步骤必须是 dotnet，不能再调用 {step.Tool}。");
            }

            foreach (var configuration in step.Configurations)
            {
                var moduleOutput = string.IsNullOrWhiteSpace(step.ModuleOutputRoot) || string.IsNullOrWhiteSpace(configuration)
                    ? ""
                    : Path.Combine(projectRoot, step.ModuleOutputRoot, configuration, "net8.0-windows");
                var isolatedBase = string.IsNullOrWhiteSpace(step.IsolatedOutputRoot)
                    ? Path.Combine(Path.GetTempPath(), "vulcan-test-output-" + Guid.NewGuid().ToString("N"))
                    : Path.Combine(projectRoot, step.IsolatedOutputRoot);
                var isolatedRoot = isolatedBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var capturePath = Path.Combine(Path.GetTempPath(), "ui-smoke-320x680-dark.png");
                var arguments = step.Arguments
                    .Select(argument => argument
                        .Replace("{configuration}", configuration, StringComparison.Ordinal)
                        .Replace("{moduleOutput}", moduleOutput, StringComparison.Ordinal)
                        .Replace("{capturePath}", capturePath, StringComparison.Ordinal)
                        .Replace("{isolatedOutputRoot}", isolatedRoot, StringComparison.Ordinal)
                        .Replace("{candidateRoot}", candidateRoot, StringComparison.Ordinal))
                    .ToList();

                if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("HISTORYVULCAN_PACKAGE_ROOT")))
                {
                    var hostProperty = "-p:HistoryVulcanPackageRoot=" +
                        Environment.GetEnvironmentVariable("HISTORYVULCAN_PACKAGE_ROOT");
                    var separator = arguments.IndexOf("--");
                    if (separator >= 0)
                        arguments.Insert(separator, hostProperty);
                    else
                        arguments.Add(hostProperty);
                }

                try
                {
                    ToolProcess.Run("dotnet", arguments, projectRoot, log,
                        step.Description.Replace("{configuration}", configuration, StringComparison.Ordinal));
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(step.IsolatedOutputRoot) && Directory.Exists(isolatedBase))
                        Directory.Delete(isolatedBase, recursive: true);
                }
            }
        }
    }
}
