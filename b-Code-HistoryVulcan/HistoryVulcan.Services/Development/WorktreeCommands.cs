using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;
using static HistoryVulcan.Services.Development.Pipeline.ToolProcess;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// AI 工作区的命令面：开分支工作树、列出、回收。
/// </summary>
/// <remarks>
/// 位置不硬编码。优先级是 <c>root=</c> 参数 &gt; 设置项 <c>ai.worktreeroot</c> &gt; 默认
/// <see cref="DefaultRoot"/>，因此默认能用、想改随时改、单次想换也不用改设置。
///
/// 分支名与目录名同形：<c>ai/&lt;项目&gt;/&lt;短SHA&gt;-&lt;序号&gt;-&lt;标识&gt;</c>。
/// 带短 SHA 是为了让工作区一眼看出基于哪个提交；带序号是因为同一提交上常并行开多个尝试，
/// 上一轮 Janus 的性能修复和工作区特性就是同一 SHA 的 -1 与 -2。
/// </remarks>
internal static class WorktreeCommands
{
    public const string DefaultRoot = @"F:\ai工作区";
    public const string KeyWorktreeRoot = "ai.worktreeroot";

    public static void Register(CommandRegistry registry, DevelopmentContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.worktree.root",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "worktree",
            Summary = "查看或设置 AI 工作区根目录（省略 path 时查询）",
            Example = @"vulcan.worktree.root path=F:\ai工作区",
            Parameters = [Text("path", "新的根目录绝对路径；省略时只查询", position: 0)],
            Handler = CommandDescriptor.Sync(context => Root(host.Settings, context.GetString("path"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.worktree.create",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "worktree",
            Summary = "为项目开一个 AI 工作区（git worktree + 新分支）",
            Example = "vulcan.worktree.create project=2026-020-HistoryJanus slug=cachekey",
            Parameters =
            [
                Text("project", "项目目录名，例如 2026-020-HistoryJanus", required: true, position: 0),
                Text("slug", "本工作区要解决的问题，短标识，只用小写字母数字和连字符", required: true, position: 1),
                Text("agent", "开这个工作区的 AI 名字，例如 claude / codex / grok", required: true, position: 2),
                Text("root", "本次使用的工作区根，省略时用设置值"),
                Bool("confirm", "项目已有闲置工作区时，仍坚持再开一个", "false"),
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var project = context.RequireString("project");
                if (!DevPipelineCommands.IsHostProject(host.Settings, project))
                    return CommandResult.Fail(DevPipelineCommands.ModuleUseStart);
                return Create(
                    host.Settings,
                    project,
                    context.RequireString("slug"),
                    context.RequireString("agent"),
                    context.GetString("root"),
                    context.GetBool("confirm"));
            }),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.worktree.list",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "worktree",
            Summary = "列出某项目已开的 AI 工作区",
            Example = "vulcan.worktree.list project=2026-020-HistoryJanus",
            Parameters = [Text("project", "项目目录名，省略时列出全部", position: 0)],
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => List(host.Settings, context.GetString("project"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.worktree.merge",
            HiddenReason = "模块开发请用 vulcan.dev.start / submit / finish。本条不对 MCP 暴露。",
            Domain = "vulcan",
            CommandClass = "worktree",
            Summary = "把 AI 工作区分支并回 main；先卸试用（含前端残留）再回收工作区，分支保留",
            Example = "vulcan.worktree.merge project=2026-020-HistoryJanus name=71c79b7-1-codex-fix",
            Parameters =
            [
                Text("project", "项目目录名", required: true, position: 0),
                Text("name", "工作区目录名", required: true, position: 1),
            ],
            Handler = async context =>
            {
                var project = context.RequireString("project");
                if (!DevPipelineCommands.IsHostProject(host.Settings, project))
                    return CommandResult.Fail(DevPipelineCommands.ModuleUseFinish);
                return await MergeAsync(
                    host,
                    project,
                    context.RequireString("name"),
                    context.Cancellation).ConfigureAwait(false);
            },
        });
    }

    private static CommandResult Root(ISettingsService settings, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return CommandResult.Ok($"AI 工作区根: {ResolveRoot(settings, null)}（默认 {DefaultRoot}）");

        var value = path.Trim();
        if (!Path.IsPathFullyQualified(value))
            return CommandResult.Fail($"必须是绝对路径：{value}");

        settings.Set(KeyWorktreeRoot, value);
        return CommandResult.Ok($"AI 工作区根已设为: {value}");
    }

    internal static CommandResult Create(
        ISettingsService settings, string project, string slug, string agent, string? rootOverride, bool confirm)
    {
        var projectName = project.Trim();
        var identifier = slug.Trim().ToLowerInvariant();
        if (identifier.Length == 0 || !identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            return CommandResult.Fail("slug 只能包含小写字母、数字和连字符。");
        var author = agent.Trim().ToLowerInvariant();
        if (author.Length == 0 || !author.All(char.IsAsciiLetterOrDigit))
            return CommandResult.Fail("agent 只能是小写字母数字，例如 claude / codex / grok。");

        var projectPath = Path.Combine(ProjectLibraryRoot.Resolve(settings), projectName);
        if (!Directory.Exists(projectPath))
            return CommandResult.Fail($"项目不存在：{projectPath}");
        if (!ProjectLibraryRoot.IsGitProject(projectPath))
            return CommandResult.Fail($"不是 git 项目：{projectPath}");

        var (head, headError) = Git(projectPath, "rev-parse", "--short", "HEAD");
        if (headError != null)
            return CommandResult.Fail($"读取 HEAD 失败：{headError}");

        var root = ResolveRoot(settings, rootOverride);
        var projectRoot = Path.Combine(root, projectName);
        Directory.CreateDirectory(projectRoot);

        // 同一提交上常并行开多个尝试。序号跳过已有目录，也跳过 finish 留下的同名分支
        // （分支一律保留，目录会删掉；只数目录会一直撞 -1- 的旧分支名）。
        if (!TryAllocateName(
                projectPath, projectRoot, projectName, head, author, identifier,
                out var name, out var worktreePath, out var branch, out var allocateError))
            return CommandResult.Fail(allocateError);

        // 克制闸门：对话窗口随时废弃，但工作区留在盘上。已经有一个"开了没干活"的工作区时，
        // 默认拒绝再开一个——那多半是上一轮对话的残留，应该接着用或先回收，而不是又堆一个。
        if (!confirm)
        {
            var idle = FindIdleWorktree(projectPath, projectRoot);
            if (idle != null)
            {
                return CommandResult.Fail(
                    $"项目已有闲置工作区 {idle}（无提交、无改动）。接着用它，或先 vulcan.dev.finish 回收；"
                    + "确实需要并行再开时传 confirm=true。");
            }
        }

        var (_, addError) = Git(projectPath, "worktree", "add", "-b", branch, worktreePath, "HEAD");
        if (addError != null)
            return CommandResult.Fail($"建工作区失败：{addError}");

        var overrideNote = WriteBuildOverride(worktreePath, settings);

        var text = new StringBuilder($"已开工作区 {name}");
        text.Append($"\n路径: {worktreePath}");
        text.Append($"\n分支: {branch}（基于 {head}）");
        text.Append($"\n项目: {projectPath}");
        text.Append($"\n{overrideNote}");
        // 5.7.0（U4）：迁根只与能切对话根的 AI 有关，其他 AI 每次读一整段只是噪声。
        if (DevPipelineCommands.MovesConversationRoot(author))
            text.Append("\ngrok 按手册四步把对话根迁进此工作区；其他 AI 不要切根，按上面的路径改文件。若对话根就是此工作区，finish 前先迁走再 vulcan.dev.finish。对话根在另一条 F 盘残留目录上，finish 这条工作区不会删掉当前根。");
        return CommandResult.Ok(text.ToString(), new
        {
            Name = name,
            Path = worktreePath,
            Branch = branch,
            BaseCommit = head,
            Project = projectName,
        });
    }

    /// <summary>
    /// 在新工作树里写下本机覆盖点，让它一诞生就能构建。
    /// </summary>
    /// <remarks>
    /// 各工程按相对路径引用宿主 z 快照，假定本仓与 2026-023-HistoryVulcan 在同一库根下；
    /// 而 AI 工作树落在库根之外，那条相对路径必然指空，表现为上百个"找不到类型"。
    /// 这里把 HistoryVulcanPackageRoot 指回来源库根的绝对路径。文件不入库（各项目 .gitignore 已登记），
    /// 因此工作树从诞生起就是干净的；项目侧没有 Directory.Build.props 时不写，避免留下无人 Import 的孤儿文件。
    /// </remarks>
    private static string WriteBuildOverride(string worktreePath, ISettingsService settings)
    {
        try
        {
            if (!File.Exists(Path.Combine(worktreePath, "Directory.Build.props")))
                return "（项目没有 Directory.Build.props，未写本机覆盖点）";

            var hostProject = Path.Combine(ProjectLibraryRoot.Resolve(settings), "2026-023-HistoryVulcan");
            var hostRoot = PublishPackages.ResolveHostSnapshot(hostProject);
            var content = $"""
                <Project>
                  <!-- 由 vulcan.worktree.create 生成：把宿主快照指回来源库根。不入库。 -->
                  <PropertyGroup>
                    <HistoryVulcanPackageRoot>{hostRoot}</HistoryVulcanPackageRoot>
                  </PropertyGroup>
                </Project>

                """;
            File.WriteAllText(Path.Combine(worktreePath, "Directory.Build.user.props"), content);
            return $"本机覆盖点已写入，宿主快照指向 {hostRoot}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"（本机覆盖点写入失败，构建时需手动传 -p:HistoryVulcanPackageRoot=…：{ex.Message}）";
        }
    }

    /// <summary>
    /// 分配未被目录占用、也未被残留分支占用的工作区名。finish 后分支保留、目录删除，
    /// 只数目录会反复撞上 <c>-1-</c> 的旧分支。
    /// </summary>
    internal static bool TryAllocateName(
        string projectPath,
        string projectRoot,
        string projectName,
        string head,
        string author,
        string identifier,
        out string name,
        out string worktreePath,
        out string branch,
        out string error)
    {
        name = "";
        worktreePath = "";
        branch = "";
        error = "";
        for (var ordinal = 1; ordinal <= 99; ordinal++)
        {
            var candidate = $"{head}-{ordinal}-{author}-{identifier}";
            var path = Path.Combine(projectRoot, candidate);
            var candidateBranch = $"ai/{projectName}/{candidate}";
            if (Directory.Exists(path) || BranchExists(projectPath, candidateBranch))
                continue;
            name = candidate;
            worktreePath = path;
            branch = candidateBranch;
            return true;
        }

        error = $"无法分配工作区名：{head}-*-{author}-{identifier} 的目录或同名分支已占满 1–99。换一个 slug 再 vulcan.dev.start。";
        return false;
    }

    private static bool BranchExists(string projectPath, string branch)
    {
        var (_, error) = Git(projectPath, "show-ref", "--verify", "--quiet", "refs/heads/" + branch);
        return error == null;
    }

    /// <summary>找出"开了但没干活"的工作区：既无未提交改动，其分支也没有领先主干的提交。</summary>
    private static string? FindIdleWorktree(string projectPath, string projectRoot)
    {
        if (!Directory.Exists(projectRoot))
            return null;

        foreach (var candidate in Directory.GetDirectories(projectRoot))
        {
            var (status, statusError) = Git(candidate, "status", "--porcelain");
            if (statusError != null || !string.IsNullOrWhiteSpace(status))
                continue;
            var (ahead, aheadError) = Git(projectPath, "rev-list", "--count", $"main..{ReadBranch(candidate)}");
            if (aheadError == null && ahead == "0")
                return Path.GetFileName(candidate);
        }

        return null;
    }

    private static string ReadBranch(string worktreePath)
    {
        var (branch, error) = Git(worktreePath, "rev-parse", "--abbrev-ref", "HEAD");
        return error == null ? branch : "HEAD";
    }

    internal static async Task<CommandResult> MergeAsync(
        DevelopmentContext host,
        string project,
        string name,
        CancellationToken cancellation)
    {
        var projectName = project.Trim();
        var worktreeName = name.Trim();
        var projectPath = Path.Combine(ProjectLibraryRoot.Resolve(host.Settings), projectName);
        if (!Directory.Exists(projectPath))
            return CommandResult.Fail($"项目不存在：{projectPath}");

        var worktreePath = ResolveWorktreePath(host.Settings, projectName, worktreeName);
        if (!Directory.Exists(worktreePath))
            return CommandResult.Fail($"工作区不存在：{worktreePath}");

        var (status, statusError) = Git(worktreePath, "status", "--porcelain");
        if (statusError != null)
            return CommandResult.Fail($"读取工作区状态失败：{statusError}");
        if (!string.IsNullOrWhiteSpace(status))
            return CommandResult.Fail("工作区还有未提交改动，先 vulcan.dev.submit。");

        var mainHead = ReadBranch(projectPath);
        if (!string.Equals(mainHead, "main", StringComparison.Ordinal))
            return CommandResult.Fail($"主树当前在 {mainHead}，合并要求主树在 main。");

        var branch = ReadBranch(worktreePath);
        if (string.IsNullOrWhiteSpace(branch) || branch == "HEAD")
            return CommandResult.Fail("无法读取工作区分支名。");

        var resolved = ReleaseCommands.TryResolveModuleByProject(
            host.Settings, projectName, out var module);
        string? moduleName = resolved ? module.Name : null;
        if (resolved && module.Kind.Equals("host", StringComparison.OrdinalIgnoreCase)
            && IsFormalHostRunning(host.Settings, out var hostExe))
        {
            return CommandResult.Fail(
                $"正式宿主正在运行，合并会替换 {hostExe}。"
                + "本命令不停止宿主；目标切换由另行授权的部署步骤处理。");
        }

        var (ffOutput, ffError) = Git(projectPath, "merge", "--ff-only", branch);
        string mergeNote;
        if (ffError == null)
        {
            mergeNote = $"已快进合并 {branch}";
        }
        else
        {
            var (mergeOutput, mergeError) = Git(projectPath, "merge", "--no-edit", branch);
            if (mergeError != null)
            {
                return CommandResult.Fail(
                    $"无法把 {branch} 并入 main：{mergeError}\n{mergeOutput}\n{ffOutput}");
            }

            mergeNote = $"已合并 {branch}（非快进）";
        }

        var removed = RemoveMergedWorktree(host.Settings, projectName, worktreeName);

        string reloadNote;
        var reloadFailed = false;
        if (resolved && module.Kind.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            reloadNote = "宿主 EXE 不随模块热重载替换；新快照在下次启动正式 HistoryVulcan.exe 时生效。";
        }
        else if (resolved)
        {
            var reload = await ModulePackageHotReload.InstallCurrentAsync(
                host, projectPath, moduleName!, "host:vulcan.worktree.merge", cancellation).ConfigureAwait(false);
            // 5.7.0（U3）：热装回执带附着核对。没接上时合并与回收已经做完、不回滚，
            // 但整条 finish 的结论必须是失败——否则「已并回」读起来就像「已上线」。
            reloadFailed = !reload.Success;
            reloadNote = reload.Success
                ? "已把合并后的版本化候选热重载到 Vulcan 运行区。\n" + LastLine(reload.Message)
                : $"合并与回收已完成，但热重载未成功（不回滚）：{reload.Message}\n请从候选或 history 再调同一热重载接口。";
        }
        else
        {
            reloadNote = "该项目不是已登记模块，不变更 Vulcan 运行区。";
        }

        var text = new StringBuilder($"{mergeNote}。\n{removed.Message}\n{reloadNote}");
        if (!removed.Success || reloadFailed)
            return CommandResult.Fail(text.ToString());
        return CommandResult.Ok(text.ToString(), new
        {
            Project = projectName,
            Branch = branch,
            Worktree = worktreePath,
        });
    }

    /// <summary>多行回执的最后一行——热装回执的附着核对结论就在那里。</summary>
    private static string LastLine(string message)
        => message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault() ?? "";

    private static CommandResult List(ISettingsService settings, string? project)
    {
        var root = ResolveRoot(settings, null);
        if (!Directory.Exists(root))
            return CommandResult.Ok($"工作区根还不存在：{root}");

        var projectDirs = string.IsNullOrWhiteSpace(project)
            ? Directory.GetDirectories(root)
            : Directory.GetDirectories(root).Where(dir =>
                string.Equals(Path.GetFileName(dir), project.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();

        var rows = new List<object>();
        var text = new StringBuilder();
        foreach (var projectDir in projectDirs.OrderBy(dir => dir, StringComparer.Ordinal))
        {
            foreach (var worktree in Directory.GetDirectories(projectDir).OrderBy(dir => dir, StringComparer.Ordinal))
            {
                var (status, _) = Git(worktree, "status", "--porcelain");
                var (branch, _) = Git(worktree, "rev-parse", "--abbrev-ref", "HEAD");
                var dirty = !string.IsNullOrWhiteSpace(status);
                text.Append($"\n  {Path.GetFileName(projectDir)}/{Path.GetFileName(worktree),-32} {(dirty ? "×有改动" : "✓干净")}  {branch}");
                rows.Add(new
                {
                    Project = Path.GetFileName(projectDir),
                    Name = Path.GetFileName(worktree),
                    Path = worktree,
                    Branch = branch,
                    Dirty = dirty,
                });
            }
        }

        return CommandResult.Ok(
            rows.Count == 0 ? $"{root} 下没有 AI 工作区。" : $"AI 工作区: {rows.Count} 个（根 {root}）{text}",
            rows);
    }

    // 仅在 MergeAsync 已确认干净并完成合并后调用；不提供独立的强制删除命令。
    private static CommandResult RemoveMergedWorktree(ISettingsService settings, string project, string name)
    {
        var projectName = project.Trim();
        var worktreeName = name.Trim();
        var projectPath = Path.Combine(ProjectLibraryRoot.Resolve(settings), projectName);
        if (!Directory.Exists(projectPath))
            return CommandResult.Fail($"项目不存在：{projectPath}");

        var worktreePath = ResolveWorktreePath(settings, projectName, worktreeName);
        if (!Directory.Exists(worktreePath)
            && FindListedWorktree(projectPath, worktreeName) is null)
        {
            return CommandResult.Fail($"工作区不存在：{Path.Combine(ResolveRoot(settings, null), projectName, worktreeName)}");
        }

        var listed = FindListedWorktree(projectPath, worktreeName) ?? worktreePath;
        var gitPath = listed;
        var arguments = new[] { "worktree", "remove", "--force", gitPath };
        var (_, error) = Git(projectPath, arguments);
        if (error != null
            && !string.Equals(gitPath, worktreePath, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(worktreePath))
        {
            var retryArgs = new[] { "worktree", "remove", "--force", worktreePath };
            var (_, retryError) = Git(projectPath, retryArgs);
            if (retryError == null)
                error = null;
            else
                error = $"{error}\n{retryError}";
        }

        if (error != null)
        {
            Git(projectPath, "worktree", "prune");
            var leftoverAfterFail = TryDeleteDirectory(worktreePath);
            if (leftoverAfterFail == null && !IsGitWorktree(projectPath, worktreeName))
            {
                return CommandResult.Ok(
                    $"git worktree remove 失败但目录已删除（{error}）。分支保留未删。",
                    new { Path = worktreePath });
            }

            return CommandResult.Fail($"移除失败：{error}" + (leftoverAfterFail == null ? "" : "\n" + leftoverAfterFail));
        }

        Git(projectPath, "worktree", "prune");
        var leftover = TryDeleteDirectory(worktreePath);
        if (!string.Equals(listed, worktreePath, StringComparison.OrdinalIgnoreCase))
        {
            var extra = TryDeleteDirectory(listed);
            leftover = leftover == null ? extra : extra == null ? leftover : leftover + "\n" + extra;
        }
        var text = $"已回收工作区 {worktreeName}，分支保留未删。";
        if (leftover != null)
            return CommandResult.Fail($"已合并，工作区未完全回收：{worktreeName}\n{leftover}");
        return CommandResult.Ok(text, new { Path = worktreePath });
    }

    internal static string ResolveWorktreePath(ISettingsService settings, string project, string nameOrPath)
    {
        var value = nameOrPath.Trim();
        if (Path.IsPathFullyQualified(value))
            return value;

        var projectPath = Path.Combine(ProjectLibraryRoot.Resolve(settings), project.Trim());
        var listed = FindListedWorktree(projectPath, value);
        if (!string.IsNullOrWhiteSpace(listed) && Directory.Exists(listed))
            return listed;

        return Path.Combine(ResolveRoot(settings, null), project.Trim(), value);
    }

    private static string? FindListedWorktree(string projectPath, string name)
    {
        if (!Directory.Exists(projectPath))
            return null;

        var (output, error) = Git(projectPath, "worktree", "list", "--porcelain");
        if (error != null)
            return null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!line.StartsWith("worktree ", StringComparison.Ordinal))
                continue;
            var path = line["worktree ".Length..];
            if (string.Equals(Path.GetFileName(path.TrimEnd('/', '\\')), name, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    private static bool IsGitWorktree(string projectPath, string name)
        => FindListedWorktree(projectPath, name) != null;

    private static string? TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return null;

        string? lastError = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (attempt > 0)
                Thread.Sleep(250);

            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                Directory.Delete(path, recursive: true);
                if (!Directory.Exists(path))
                    return null;
                lastError = "目录删除后仍存在";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                lastError = ex.Message;
            }
        }

        return $"残留目录未能删除：{path}（{lastError}）";
    }

    private static bool IsFormalHostRunning(ISettingsService settings, out string executable)
    {
        var projectRoot = Path.Combine(
            ProjectLibraryRoot.Resolve(settings),
            "2026-023-HistoryVulcan");
        try
        {
            executable = Path.Combine(
                PublishPackages.ResolveHostSnapshot(projectRoot),
                "host",
                "HistoryVulcan.exe");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or DirectoryNotFoundException)
        {
            // 4.0.0 keeps the formal host snapshot flat; retain a deterministic
            // diagnostic path when the candidate is absent or malformed.
            executable = Path.Combine(projectRoot, "z-Publish", "host", "HistoryVulcan.exe");
            return false;
        }
        if (!File.Exists(executable))
            return false;

        var expected = Path.GetFullPath(executable);
        foreach (var process in Process.GetProcessesByName("HistoryVulcan"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    internal static string ResolveRoot(ISettingsService settings, string? rootOverride)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride) && Path.IsPathFullyQualified(rootOverride.Trim()))
            return rootOverride.Trim();
        var configured = settings.Get(KeyWorktreeRoot);
        return string.IsNullOrWhiteSpace(configured) ? DefaultRoot : configured.Trim();
    }

    private static ParameterSpec Text(string name, string description, bool required = false, int? position = null)
        => new() { Name = name, Description = description, Required = required, Position = position };

    private static ParameterSpec Bool(string name, string description, string defaultValue)
        => new() { Name = name, Description = description, Type = ParamType.Bool, Default = defaultValue };
}
