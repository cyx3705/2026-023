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
            Summary = "列出项目库里自带 publish 描述的模块（现场扫描，宿主不保存）",
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
                if (!ReleaseCatalog.IsHost(name))
                    return CommandResult.Fail(DevPipelineCommands.ModuleUseSubmitFinish);
                return await CycleAsync(
                    host,
                    ReleaseCatalog.Host(),
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
        var errors = new List<string>();
        var rows = ReleaseCatalog.Discover(ProjectLibraryRoot.Resolve(settings), errors);
        var text = new StringBuilder($"自带 publish 描述的模块: {rows.Count} 个");
        foreach (var row in rows)
            text.Append($"\n  {row.Name,-18} {row.ProjectDirectory}");
        foreach (var error in errors)
            text.Append("\n  描述有误 ").Append(error);
        return CommandResult.Ok(text.ToString(), rows.Select(row =>
            new { row.Name, row.Kind, Project = row.ProjectDirectory }).ToList());
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
        var failure = "";
        using (var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            try
            {
                var hostSnapshot = PublishPackages.ResolveHostSnapshot(PipelineProjectRoot(host.Settings));
                ReleaseEngine.Execute(
                    new ReleaseRequest(target, projectRoot,
                        target.Kind == "host" ? FreezePath(host.Settings) : null,
                        hostSnapshot, publish, RequireCleanSource: false), log);
                CommitAfterPipeline(projectRoot, commitMessage, log);
                exit = 0;
            }
            catch (Exception ex)
            {
                log.WriteLine(ex.ToString());
                failure = ex.Message;
            }
        }

        File.WriteAllText(logPath + ExitFileSuffix, exit.ToString(), Encoding.ASCII);
        return exit == 0
            ? CommandResult.Ok($"发布管线完成。run={run}\n日志: {logPath}")
            : CommandResult.Fail($"发布管线失败：{Brief(failure, FailureLines)}\n完整日志: {logPath}");
    }

    /// <summary>失败回执里带的原因行数。原因就在回执里，调用方多数时候不必再去翻日志（5.8.0，REQ-HOST-079）。</summary>
    private const int FailureLines = 12;

    /// <summary>截到前 <paramref name="lines"/> 行；多出的只报行数。</summary>
    internal static string Brief(string text, int lines)
    {
        var all = text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return all.Length <= lines
            ? string.Join("\n", all)
            : string.Join("\n", all.Take(lines)) + $"\n…另有 {all.Length - lines} 行，见日志";
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

    /// <summary>
    /// 宿主冻结标签表的位置。它只守宿主自己的冻结标签；模块的发布描述在模块仓的 project.manifest.json（DEC-069）。
    /// </summary>
    internal const string FreezeFile = "b-Code-Eng/pipeline/host-freeze.json";

    private static string PipelineProjectRoot(ISettingsService settings)
        => Path.Combine(ProjectLibraryRoot.Resolve(settings), ReleaseCatalog.HostProjectDirectory);

    private static string FreezePath(ISettingsService settings)
        => Path.Combine(PipelineProjectRoot(settings), FreezeFile);

    private static string LogDirectory(DevelopmentContext host)
        => Path.Combine(host.DataDirectory, "release");

    internal static async Task<CommandResult> CycleAsync(
        DevelopmentContext host,
        ReleaseTarget module,
        string message,
        string? worktree,
        bool allowDirty,
        bool dryRun,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        var moduleName = module.Name;
        var commitMessage = message.Trim();
        if (commitMessage.Length == 0)
            return CommandResult.Fail("msg 不能为空。");

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
            var candidatePath = Path.Combine(repoRoot, module.FormalDirectory);
            var files = module.Package?.Files ?? [];
            var tests = module.Validation.Select(step => step.Description).ToList();
            var runtimeTarget = module.Kind.Equals("module", StringComparison.OrdinalIgnoreCase)
                ? "runtime module package (no reload in dry-run)"
                : "host candidate (restart required; no reload in dry-run)";
            return CommandResult.Ok(
                $"dry-run: {moduleName}\n项目: {repoRoot}\n候选: {candidatePath}\n运行时目标: {runtimeTarget}\n"
                + $"测试: {(tests.Count == 0 ? "无额外模块测试" : string.Join("; ", tests))}\n"
                + $"文件摘要: {(files.Count == 0 ? "由发布输出决定" : string.Join(", ", files))}\n将执行阶段: {string.Join(", ", stages)}\n"
                + (status.Length == 0 ? "脏树: 否\n" : $"将提交: {FileSummary(status)}\n")
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
            text.Append("\n已提交: ").Append(FileSummary(status));
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
        // 5.8.0（REQ-HOST-079）：成功只留热装核对结论一行；下一步怎么走由 vulcan.dev.submit 统一给一次。
        text.Append('\n').Append(reload.Success
            ? LastLine(reload.Message)
            : "候选已提交，但 Vulcan 热重载未成功（不自动回滚）：" + reload.Message);

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
    internal static CommandResult VerifySubmittedCandidate(ReleaseTarget target, string repoRoot)
    {
        var version = ReleaseCatalog.ReadVersion(repoRoot, target);
        var candidate = Path.Combine(repoRoot, target.CandidateDirectory, $"{target.Name}-v{version}");
        if (!Directory.Exists(candidate))
            return CommandResult.Fail($"未找到已审核候选：{candidate}。请先执行 vulcan.dev.submit。");

        try
        {
            ProjectContract.Validate(repoRoot, target.Kind, instantiation: true, freezePath: null);
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

    /// <summary>
    /// 工作区优先、主树兜底地读模块的 publish 描述：描述跟着模块代码走，本轮改了就用本轮的；
    /// 5.8.0 之前开出的工作区里还没有这一节，才退回主树。
    /// </summary>
    internal static bool TryLoadModule(
        ISettingsService settings, string projectDirectory, string? worktreePath,
        out ReleaseTarget module, out string error)
    {
        module = default!;
        error = "";
        var mainTree = Path.Combine(ProjectLibraryRoot.Resolve(settings), projectDirectory);
        try
        {
            var loaded = (worktreePath != null ? ReleaseCatalog.TryLoadModule(worktreePath, projectDirectory) : null)
                         ?? ReleaseCatalog.TryLoadModule(mainTree, projectDirectory);
            if (loaded == null)
            {
                error = $"项目 {projectDirectory} 的 project.manifest.json 没有 publish 节。"
                        + "模块自带发布描述，写法见模块开发手册「发布描述」。";
                return false;
            }

            module = loaded;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            error = $"项目 {projectDirectory} 的发布描述读不出来：{ex.Message}";
            return false;
        }
    }

    /// <summary>按模块名在项目库里找：只在工作区反查不到项目、又写了 name= 时用。</summary>
    internal static bool TryFindModuleByName(ISettingsService settings, string name, out ReleaseTarget module)
    {
        module = ReleaseCatalog.Discover(ProjectLibraryRoot.Resolve(settings))
            .FirstOrDefault(target => target.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return module != null;
    }

    /// <summary>
    /// 脏文件只报个数和前几个路径（5.8.0，REQ-HOST-079）：dev 工作区里的改动全是本轮要提交的，
    /// 整份 git status 抄进回执对调用方没有信息量。
    /// </summary>
    internal static string FileSummary(string status, int shown = 5)
    {
        var files = status.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 3)
            .Select(line => line[3..].Trim())
            .ToList();
        var head = string.Join("、", files.Take(shown));
        return files.Count <= shown ? $"{files.Count} 个文件（{head}）" : $"{files.Count} 个文件（{head} 等）";
    }

    private static string LastLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";

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
