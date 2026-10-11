using System.Text;
using System.Text.Json;
using HistoryVulcan.Services.Modules;

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
            throw new InvalidOperationException($"{target.Name} 未声明 package（project/publishTargetFramework/files）。");

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
        var (targetFramework, output) = MsBuildOutputResolver.Resolve(projectRoot, target.Package, log);
        ToolProcess.Run(
            "dotnet",
            ["build", project, "-c", "Release", "-f", targetFramework, "--nologo", "-p:NuGetAudit=false",
                "-p:HistoryVulcanPackageRoot=" + hostSnapshotRoot],
            projectRoot,
            log,
            "构建模块候选包");

        if (!Directory.Exists(output))
            throw new InvalidOperationException($"构建后实际输出不存在：{output}");

        Directory.CreateDirectory(stagingRoot);
        var files = new List<(string Relative, string SourcePath)>();
        foreach (var name in target.Package.Files)
        {
            var source = Path.Combine(output, name);
            if (!File.Exists(source))
                throw new InvalidOperationException($"缺少预期产物：{source}");
            files.Add((name.Replace('\\', '/'), source));
        }

        // 7.0.0：候选就是 nupkg。本机安装和发布归档用的是这一个文件，不再另抄目录并写 SHA256SUMS。
        var nupkg = Path.Combine(stagingRoot, target.Name + ModuleNupkg.Extension);
        ModuleNupkg.Pack(nupkg, target.Name, version, files);
        AssertSnapshot(stagingRoot, target, version);
        return stagingRoot;
    }

    public static void AssertSnapshot(string root, ReleaseTarget target, string version)
    {
        var packages = Directory.Exists(root)
            ? Directory.GetFiles(root, "*" + ModuleNupkg.Extension, SearchOption.TopDirectoryOnly)
            : [];
        if (packages.Length != 1)
            throw new InvalidOperationException($"模块快照不完整：{root}");

        if (!ModuleNupkg.TryRead(packages[0], out var entry, out _, out var error))
            throw new InvalidOperationException($"模块快照无法读取：{error}");
        if (!entry.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
            || !entry.Version.Equals(version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"模块快照身份不匹配：期望 {target.Name} {version}");
        }
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
