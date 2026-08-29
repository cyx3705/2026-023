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
        AssertNotSelfReplacing(publishRoot, target.Name);
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
                candidate = PublishLayout.PromoteVersioned(
                    staging, publishRoot, target, version, replaceCurrent: !request.PromoteOfficial);
                ModuleSnapshotBuilder.AssertSnapshot(candidate, target, version);
            }

            if (!request.PromoteOfficial)
            {
                log.WriteLine($"工作区版本化候选已就绪：{candidate}");
                log.WriteLine("vulcan.dev.submit 会把该候选热重载进活宿主。");
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

    /// <summary>
    /// 拒绝替换当前进程正在运行的那份包。
    ///
    /// 宿主自替换按 DEC-053 走**手动操作**：不做影子目录，也不做独立重启器——
    /// 宿主往后极少更新，为一年一次的动作在发布路径里长出一套切换机制不划算。
    ///
    /// 但「不自动」不等于「撞上去再说」。此前没有任何前置检查：从
    /// z-Publish\host\HistoryVulcan.exe 起的宿主执行 vulcan.release.cycle
    /// name=HistoryVulcan，会先跑完还原、单元测试、质量门禁、公开 API 门禁和一次完整
    /// publish（几分钟），最后在 PromoteFlatHost 里撞上自己 exe 的文件锁而失败回滚。
    /// 结论正确，代价是几分钟白跑，而抛出的 IOException 说的是「文件被占用」，
    /// 没有一个字提到「你正在替换你自己」。
    ///
    /// 现在在第一步就说破，并给出唯一可行的做法。
    /// </summary>
    private static void AssertNotSelfReplacing(string publishRoot, string targetName)
    {
        var running = Environment.ProcessPath;
        if (string.IsNullOrEmpty(running))
            return;

        var root = Path.GetFullPath(publishRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(running).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(
            $"拒绝发布 {targetName}：发布目标就是当前进程运行的目录。" + Environment.NewLine
            + $"  当前进程：{running}" + Environment.NewLine
            + $"  发布目标：{publishRoot}" + Environment.NewLine
            + "宿主自替换按 DEC-053 走手动操作。请先停服，"
            + "再从该目录之外的宿主（例如 App 项目的 bin/Release 开发构建）执行本次发布。");
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
