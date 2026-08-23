namespace HistoryVulcan.Services.Development.Pipeline;

internal sealed record ReleaseRequest(
    string ModuleName,
    string ProjectRoot,
    string RegistryPath,
    string HostSnapshotRoot,
    bool PromoteOfficial,
    bool RequireCleanSource);

internal static class ReleaseEngine
{
    public static string Execute(ReleaseRequest request, TextWriter log)
    {
        if (request.PromoteOfficial && !request.ProjectRoot.Equals(
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(request.RegistryPath)!, "..", "..", "..")),
                StringComparison.OrdinalIgnoreCase))
        {
            // 正式促级只从主树来；调用方已把 worktree 与 publish 互斥掉。
        }

        var target = ReleaseCatalog.Require(request.RegistryPath, request.ModuleName);
        var projectRoot = Path.GetFullPath(request.ProjectRoot);
        var version = ReleaseCatalog.ReadVersion(projectRoot, target);
        var publishRoot = Path.Combine(projectRoot, target.CandidateDirectory);
        var workRoot = Path.Combine(projectRoot, ".publish-stage", "module-release-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            AssertGitState(projectRoot, target, request.RequireCleanSource, log);
            ProjectContract.Validate(
                projectRoot,
                target.Kind,
                instantiation: true,
                target.Kind == "host" ? request.RegistryPath : null);

            string candidate;
            if (target.Kind == "host")
            {
                QualityGates.AssertSourceQuality(projectRoot, log);
                QualityGates.AssertPublicApiBaseline(projectRoot, version, log);
                ToolProcess.Run(
                    "dotnet",
                    ["restore", Path.Combine(projectRoot, "HistoryVulcan.sln"), "--locked-mode", "--nologo",
                        "-p:NuGetAudit=false"],
                    projectRoot,
                    log,
                    "还原宿主解决方案");
                ToolProcess.Run(
                    "dotnet",
                    ["test", Path.Combine(projectRoot, target.TestProject), "-c", "Release", "--nologo",
                        "--no-restore", "-p:NuGetAudit=false"],
                    projectRoot,
                    log,
                    "运行宿主单元测试");
                candidate = HostSnapshotBuilder.Build(projectRoot, version, publishRoot, log);
            }
            else
            {
                var staging = Path.Combine(workRoot, $"{target.Name}-v{version}");
                ModuleSnapshotBuilder.Build(
                    projectRoot, target, version, staging, request.HostSnapshotRoot, log);
                ModuleSnapshotBuilder.RunValidation(target, projectRoot, staging, log);
                candidate = PublishLayout.PromoteVersioned(staging, publishRoot, target, version);
                ModuleSnapshotBuilder.AssertSnapshot(candidate, target, version);
            }

            if (!request.PromoteOfficial)
            {
                log.WriteLine($"工作区版本化候选已就绪：{candidate}");
                log.WriteLine("vulcan.release.cycle 会用该候选严格替换 AppData 运行包。");
                return candidate;
            }

            log.WriteLine($"已发布候选 {target.Name} {version}：{candidate}");
            if (target.Kind == "host")
            {
                var exe = Path.Combine(candidate, "host", "HistoryVulcan.exe");
                if (File.Exists(exe))
                    ToolProcess.Run(exe, ["--repair-autostart"], candidate, log, "修复登录启动");
            }

            return candidate;
        }
        finally
        {
            if (Directory.Exists(workRoot))
                Directory.Delete(workRoot, recursive: true);
        }
    }

    private static void AssertGitState(
        string projectRoot,
        ReleaseTarget target,
        bool requireClean,
        TextWriter log)
    {
        var status = ToolProcess.Capture(
            "git",
            ["status", "--porcelain", "--", $":!{target.CandidateDirectory}/**", $":!{target.FormalDirectory}/**"],
            projectRoot);
        if (status.Length > 0 && requireClean)
            throw new InvalidOperationException("源工作树不干净。默认是先部署再提交；需要干净 HEAD 才传 requireClean。\n" + status);
        if (status.Length > 0)
            log.WriteLine($"警告: 正在从不干净的工作树发布 {target.Name}。发布后请把源码与 z 一起提交。");

        var commit = ToolProcess.Capture("git", ["rev-parse", "HEAD"], projectRoot);
        if (string.IsNullOrWhiteSpace(commit))
            throw new InvalidOperationException($"无法解析源提交：{projectRoot}");
    }
}
