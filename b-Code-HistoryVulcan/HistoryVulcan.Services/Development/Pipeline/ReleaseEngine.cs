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
        // 4.8/5.0 之前这里是一个**空的 if**：条件算完就丢，函数体里只有一行注释说
        // 「正式促级只从主树来；调用方已把 worktree 与 publish 互斥掉」。那不是纵深防御，
        // 是立了一把从不上锁的锁。而且它把未规范化的 request.ProjectRoot 与
        // Path.GetFullPath(...) 的结果相比，即便补上 throw 也永远不会相等。
        //
        // 判据交由调用方持有——它才知道这次是主树还是工作区——宿主这里不再假装校验。
        // 需要重新立这道闸口时，要连同「主树路径如何认定」一起设计，而不是补一个 throw。

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
