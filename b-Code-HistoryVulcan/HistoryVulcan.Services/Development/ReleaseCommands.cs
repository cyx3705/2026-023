using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Development.Pipeline;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 发布管线的命令面：在宿主进程内跑构建、合同、门禁并写入候选。
/// </summary>
/// <remarks>管线同步完成构建、门禁与提交后返回；status/log 从磁盘读取日志与 .exit 结果。</remarks>
internal static class ReleaseCommands
{
    private const string ExitFileSuffix = ".exit";
    private const string PipelineProjectName = "2026-023-HistoryVulcan";

    public static void Register(CommandRegistry registry, DevelopmentContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.modules",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "列出可发布的模块和宿主及其项目目录",
            Example = "vulcan.release.modules",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => Modules(host.Settings)),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.status",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "查看发布运行状态：仍在跑 / 成功 / 失败，附日志末尾",
            Example = "vulcan.release.status",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "附带的日志末尾行数，范围 1~200", "20"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Status(
                host,
                context.GetString("run"),
                context.GetInt("lines", 20))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.log",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "读取某次发布运行的日志末尾",
            Example = "vulcan.release.log run=20260815-090000-HistoryMercury lines=80",
            Parameters =
            [
                Text("run", "run 标识，省略时取最近一次", position: 0),
                Int("lines", "返回的末尾行数，范围 1~500", "80"),
            ],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => Log(
                host,
                context.GetString("run"),
                context.GetInt("lines", 80))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.release.cycle",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。宿主打包仍可用 --cli vulcan.release.cycle。",
            Domain = "vulcan",
            CommandClass = "release",
            Summary = "跑通门禁、写入版本化候选并提交；模块候选严格替换到 Vulcan 运行区",
            Example = "vulcan.release.cycle name=HistoryVulcan msg=candidate worktree=abc-1-grok-fix",
            Parameters =
            [
                Text("name", "只接受宿主 HistoryVulcan；模块请用 vulcan.dev.submit / finish", required: true, position: 0),
                Text("msg", "提交说明", required: true, position: 1),
                Text("worktree", "AI 工作区目录名或绝对路径；省略则在主树正式促级后提交"),
                Bool("allowDirty", "允许从有未提交变更的工作树提交", "false"),
                Bool("dryRun", "只预检，不写文件、不构建、不提交、不热重载", "false"),
            ],
            Handler = async context =>
            {
                var name = context.RequireString("name");
                if (!DevPipelineCommands.IsHostProject(host.Settings, name))
                    return CommandResult.Fail(DevPipelineCommands.ModuleUseSubmitFinish);
                return await CycleAsync(
                    host,
                    name,
                    context.RequireString("msg"),
                    context.GetString("worktree"),
                    context.GetBool("allowDirty"),
                    context.GetBool("dryRun"),
                    context.Progress,
                    context.Cancellation).ConfigureAwait(false);
            },
        });
    }

    private static CommandResult Modules(ISettingsService settings)
    {
        var registryPath = RegistryPath(settings);
        if (!File.Exists(registryPath))
            return CommandResult.Fail($"找不到发布登记表：{registryPath}");

        try
        {
            var rows = ReleaseCatalog.Load(registryPath);
            var text = new StringBuilder($"已登记发布目标: {rows.Count} 个");
            foreach (var row in rows)
                text.Append($"\n  {row.Name,-18} {row.Kind,-8} {row.ProjectDirectory}");
            return CommandResult.Ok(text.ToString(), rows.Select(row =>
                new { row.Name, row.Kind, Project = row.ProjectDirectory }).ToList());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return CommandResult.Fail($"发布登记表解析失败：{ex.Message}");
        }
    }

    private static CommandResult RunPipeline(
        DevelopmentContext host, ReleaseTarget target, string projectRoot, bool publish,
        string commitMessage, out string run)
    {
        var logDirectory = LogDirectory(host);
        Directory.CreateDirectory(logDirectory);
        run = $"{DateTime.Now:yyyyMMdd-HHmmss}-{target.Name}-{Guid.NewGuid():N}";
        var logPath = Path.Combine(logDirectory, run + ".log");
        var exit = 1;
        using (var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                var hostSnapshot = PublishPackages.ResolveHostSnapshot(PipelineProjectRoot(host.Settings));
                ReleaseEngine.Execute(
                    new ReleaseRequest(target.Name, projectRoot, RegistryPath(host.Settings),
                        hostSnapshot, publish, RequireCleanSource: false), log);
                CommitAfterPipeline(projectRoot, commitMessage, log);
                exit = 0;
            }
            catch (Exception ex)
            {
                log.WriteLine(ex.ToString());
            }
        }

        File.WriteAllText(logPath + ExitFileSuffix, exit.ToString(), Encoding.ASCII);
        return exit == 0
            ? CommandResult.Ok($"发布管线完成。run={run}\n日志: {logPath}")
            : CommandResult.Fail($"发布管线失败。run={run}\n日志: {logPath}");
    }

    private static void CommitAfterPipeline(string commitRoot, string commitMessage, TextWriter log)
    {
        ToolProcess.Run("git", ["add", "-A"], commitRoot, log, "暂存发布结果");
        var dirty = ToolProcess.RunAllowingFailure(
            "git", ["diff", "--cached", "--quiet"], commitRoot, log, "检查暂存区");
        if (dirty == 0)
        {
            log.WriteLine("管线结束后没有需要提交的变更。");
            return;
        }

        var msgPath = Path.Combine(Path.GetTempPath(), "vulcan-release-" + Guid.NewGuid().ToString("N") + ".msg");
        File.WriteAllText(msgPath, commitMessage.Trim() + Environment.NewLine, new UTF8Encoding(false));
        try
        {
            ToolProcess.Run(
                "git",
                ["-c", "i18n.commitEncoding=utf-8", "commit", "-F", msgPath],
                commitRoot,
                log,
                "提交源码与候选");
        }
        finally
        {
            File.Delete(msgPath);
        }
    }

    private static CommandResult Status(DevelopmentContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 200), out var exitCode, out var finished);
        var runId = Path.GetFileNameWithoutExtension(logPath!);
        var state = !finished
            ? "仍在运行"
            : exitCode == 0 ? "成功" : $"失败（退出码 {exitCode}）";

        var text = new StringBuilder($"[{runId}] {state}");
        text.Append($"\n日志: {logPath}");
        text.Append($"\n--- 末尾 {tail.Count} 行 ---");
        foreach (var line in tail)
            text.Append('\n').Append(line);

        return CommandResult.Ok(text.ToString(), new
        {
            Run = runId,
            Finished = finished,
            ExitCode = exitCode,
            Success = finished && exitCode == 0,
            Log = logPath,
        });
    }

    private static CommandResult Log(DevelopmentContext host, string? run, int lines)
    {
        var (logPath, error) = ResolveRun(host, run);
        if (error != null)
            return CommandResult.Fail(error);

        var tail = ReadTail(logPath!, Math.Clamp(lines, 1, 500), out _, out _);
        var text = new StringBuilder($"[{Path.GetFileNameWithoutExtension(logPath!)}] 末尾 {tail.Count} 行");
        foreach (var line in tail)
            text.Append('\n').Append(line);
        return CommandResult.Ok(text.ToString(), new { Log = logPath, Lines = tail });
    }

    private static (string? LogPath, string? Error) ResolveRun(DevelopmentContext host, string? run)
    {
        var logDirectory = LogDirectory(host);
        if (!Directory.Exists(logDirectory))
            return (null, "还没有任何发布运行记录。");

        if (!string.IsNullOrWhiteSpace(run))
        {
            var named = Path.Combine(logDirectory, run.Trim() + ".log");
            return File.Exists(named) ? (named, null) : (null, $"找不到运行记录 {run.Trim()}。");
        }

        var newest = new DirectoryInfo(logDirectory).GetFiles("*.log")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        return newest == null ? (null, "还没有任何发布运行记录。") : (newest.FullName, null);
    }

    /// <summary>读日志末尾；允许被子进程同时写入，因此以共享模式打开。</summary>
    internal static List<string> ReadTail(string path, int lines, out int exitCode, out bool finished)
    {
        exitCode = 0;
        finished = false;
        var tail = new Queue<string>(lines);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == lines)
                    tail.Dequeue();
                tail.Enqueue(line);
            }
        }
        catch (IOException)
        {
            return ["(日志暂时不可读，管线正在写入)"];
        }

        var exitPath = path + ExitFileSuffix;
        if (File.Exists(exitPath))
        {
            try
            {
                finished = int.TryParse(File.ReadAllText(exitPath).Trim(), out var parsed);
                exitCode = finished ? parsed : 0;
            }
            catch (IOException)
            {
            }
        }

        return tail.ToList();
    }

    /// <summary>发布登记表在项目里的位置。管线实现已迁入宿主源码，不再调用 PowerShell 引擎脚本。</summary>
    private const string PipelineDirectory = "b-Code-Eng/pipeline";

    private static string PipelineProjectRoot(ISettingsService settings)
        => Path.Combine(ProjectLibraryRoot.Resolve(settings), PipelineProjectName);

    private static string RegistryPath(ISettingsService settings)
        => Path.Combine(PipelineProjectRoot(settings), PipelineDirectory, "module-publish.manifest.json");

    private static string LogDirectory(DevelopmentContext host)
        => Path.Combine(host.DataDirectory, "release");

    internal static async Task<CommandResult> CycleAsync(
        DevelopmentContext host,
        string name,
        string message,
        string? worktree,
        bool allowDirty,
        bool dryRun,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var moduleName = name.Trim();
        var commitMessage = message.Trim();
        if (commitMessage.Length == 0)
            return CommandResult.Fail("msg 不能为空。");

        if (!TryResolveModule(host.Settings, moduleName, out var module))
            return CommandResult.Fail($"{moduleName} 不在发布登记表里，见 vulcan.release.modules。");

        var mainProject = Path.Combine(ProjectLibraryRoot.Resolve(host.Settings), module.ProjectDirectory);
        var isWorktree = !string.IsNullOrWhiteSpace(worktree);
        string repoRoot;
        if (isWorktree)
        {
            repoRoot = WorktreeCommands.ResolveWorktreePath(
                host.Settings, module.ProjectDirectory, worktree!.Trim());
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"工作区不存在：{repoRoot}");
        }
        else
        {
            repoRoot = mainProject;
            if (!Directory.Exists(repoRoot))
                return CommandResult.Fail($"项目主树不存在：{repoRoot}");
        }

        // 5.7.0（U4）：只裁尾部换行。Capture 的 Trim() 会吃掉首行状态列前的空格，
        // 清单于是首行写成「M a」、其余写成「 M b」。
        var status = ToolProcess.CaptureLines(
            "git", ["status", "--porcelain", "--", $":!{module.FormalDirectory}/**"], repoRoot);
        if (status.Length > 0 && !allowDirty)
            return new CommandResult
            {
                Success = false,
                Message = $"工作树不干净，已拒绝提交（退出码 2）。请传 allowDirty=true。\n{status}",
                Data = new { ExitCode = 2, DirtyFiles = DirtyFiles(status) },
            };

        if (dryRun)
        {
            var stages = new[] { "contract", "build", "test", "candidate", "commit", "runtime-reload" };
            var target = ReleaseCatalog.Require(RegistryPath(host.Settings), moduleName);
            var candidatePath = Path.Combine(repoRoot, module.FormalDirectory);
            var files = target.Package?.Files ?? [];
            var tests = target.Validation.Select(step => step.Description).ToList();
            var runtimeTarget = module.Kind.Equals("module", StringComparison.OrdinalIgnoreCase)
                ? "runtime module package (no reload in dry-run)"
                : "host candidate (restart required; no reload in dry-run)";
            return CommandResult.Ok(
                $"dry-run: {moduleName}\n项目: {repoRoot}\n候选: {candidatePath}\n运行时目标: {runtimeTarget}\n"
                + $"测试: {(tests.Count == 0 ? "无额外模块测试" : string.Join("; ", tests))}\n"
                + $"文件摘要: {(files.Count == 0 ? "由发布输出决定" : string.Join(", ", files))}\n将执行阶段: {string.Join(", ", stages)}\n"
                + $"脏树: {(status.Length == 0 ? "否" : "是（已授权）")}\n"
                + (status.Length == 0 ? "" : $"脏树文件:\n{status}\n")
                + "不会写日志、候选、运行区、暂存区或触发热重载。",
                new
                {
                    Run = Guid.NewGuid().ToString("N"),
                    Module = moduleName,
                    Project = repoRoot,
                    CandidatePath = candidatePath,
                    RuntimeTarget = runtimeTarget,
                    Files = files,
                    Tests = tests,
                    Dirty = status.Length > 0,
                    DirtyFiles = DirtyFiles(status),
                    AllowDirty = allowDirty,
                    DryRun = true,
                    Stages = stages
                });
        }

        cancellation.ThrowIfCancellationRequested();
        progress?.Report($"正在运行 {moduleName} 的门禁和提交…");
        var completed = RunPipeline(host, module, repoRoot, !isWorktree, commitMessage, out var run);
        if (!completed.Success)
            return completed;

        var text = new StringBuilder($"cycle 完成：{moduleName} 已门禁通过并提交。\n仓库: {repoRoot}\nrun={run}");
        if (status.Length > 0)
            text.Append("\n已授权脏树文件:\n").Append(status);
        if (!string.Equals(module.Kind, "module", StringComparison.OrdinalIgnoreCase))
        {
            text.Append(isWorktree
                ? "\n宿主候选不热重载模块槽，可继续在工作区开发或 vulcan.worktree.merge。"
                : "\n主树 z-Publish 宿主候选已通过门禁；重启切换由宿主部署步骤完成。");
            if (isWorktree)
                text.Append("\n若对话根已在工作区内（grok 切过根），合并前先迁出。");
            return CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = isWorktree ? repoRoot : null,
                Run = run,
                Committed = true,
            });
        }

        progress?.Report("提交完成，正在调用 Vulcan 热重载当前候选…");
        var reload = await ModulePackageHotReload.InstallCurrentAsync(
            host, repoRoot, moduleName, "host:vulcan.release.cycle", CancellationToken.None).ConfigureAwait(false);
        text.Append('\n').Append(reload.Success
            ? reload.Message
            : "候选已提交，但 Vulcan 热重载未成功（不自动回滚）：" + reload.Message);
        text.Append("\n测试不通过时，从主树候选或 z-Publish/history 再调同一热重载接口，不会自动恢复。");
        if (isWorktree)
        {
            // 迁根提示由 vulcan.dev.submit 按工作区的 agent 决定是否追加（5.7.0，U4）。
            text.Append("\n可继续在此工作区开发。人审批通过后 vulcan.dev.finish。");
        }

        return reload.Success
            ? CommandResult.Ok(text.ToString(), new
            {
                Module = moduleName,
                Worktree = isWorktree ? repoRoot : null,
                Run = run,
                Committed = true,
            })
            : CommandResult.Fail(text.ToString());
    }

    /// <summary>
    /// 复核 submit 已经写入工作区的不可变模块候选，供 finish 在合并前使用。
    /// finish 不能再次构建同一版本：submit 在构建后提交源码，第二次构建可能因源修订元数据改变程序集，
    /// 而不可变快照必须拒绝覆盖。
    /// </summary>
    internal static CommandResult VerifySubmittedCandidate(
        DevelopmentContext host,
        string moduleName,
        string repoRoot)
    {
        var target = ReleaseCatalog.Require(RegistryPath(host.Settings), moduleName);
        var version = ReleaseCatalog.ReadVersion(repoRoot, target);
        var candidate = Path.Combine(repoRoot, target.CandidateDirectory, $"{target.Name}-v{version}");
        if (!Directory.Exists(candidate))
            return CommandResult.Fail($"未找到已审核候选：{candidate}。请先执行 vulcan.dev.submit。");

        try
        {
            ProjectContract.Validate(repoRoot, target.Kind, instantiation: true, RegistryPath(host.Settings));
            ModuleSnapshotBuilder.AssertSnapshot(candidate, target, version);
            return CommandResult.Ok(
                $"已复核 submit 候选：{target.Name} {version}\n候选: {candidate}",
                new { Module = target.Name, Version = version, Candidate = candidate });
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"已审核候选不可用于 finish：{ex.Message}");
        }
    }

    private static IReadOnlyList<string> DirtyFiles(string status)
        => status.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static bool TryResolveModule(ISettingsService settings, string name, out ReleaseTarget module)
        => TryResolveTarget(settings, name, byProject: false, out module);

    internal static bool TryResolveModuleByProject(ISettingsService settings, string projectDirectory, out ReleaseTarget module)
        => TryResolveTarget(settings, projectDirectory, byProject: true, out module);

    private static bool TryResolveTarget(ISettingsService settings, string value, bool byProject, out ReleaseTarget module)
    {
        module = default!;
        try
        {
            var path = RegistryPath(settings);
            var target = !byProject ? ReleaseCatalog.Require(path, value)
                : value.Equals(PipelineProjectName, StringComparison.OrdinalIgnoreCase)
                    ? ReleaseCatalog.RequireHost(path)
                    : ReleaseCatalog.LoadModules(path).FirstOrDefault(item =>
                        item.ProjectDirectory.Equals(value, StringComparison.OrdinalIgnoreCase));
            if (target == null || string.IsNullOrWhiteSpace(target.ProjectDirectory)
                               || string.IsNullOrWhiteSpace(target.FormalDirectory))
                return false;
            module = target;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Required = required, Position = position };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Bool,
            Default = defaultValue,
            AllowedValues = ["true", "false"],
        };

    private static ParameterSpec Int(string name, string description, string defaultValue)
        => new() { Name = name, Description = description, Type = ParamType.Int, Default = defaultValue };
}
