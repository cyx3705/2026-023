using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Development.Pipeline;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// 模块开发管线的公开三步：开工作区、送审、通过后并回。
/// </summary>
/// <remarks>
/// 图面只留三条，且<strong>不配 MCP</strong>：只走同目录 Console <c>HistoryVulcan.Cli.exe --cli</c>。
/// 内部仍调用 <see cref="WorktreeCommands"/> 与 <see cref="ReleaseCommands"/>。
/// HistoryVulcan 自身不走这三条，宿主打包用 <c>--cli vulcan.release.cycle</c>。
///
/// 5.7.0 起 submit / finish 只需要 <c>worktree=</c>：模块由工作区所在项目反查发布登记表，
/// 提交说明缺省取工作区名里的 slug（REQ-HOST-072）。
/// </remarks>
internal static class DevPipelineCommands
{
    internal const string HostRejected =
        "HistoryVulcan 不走模块开发管线。宿主打包用 HistoryVulcan.Cli.exe --cli vulcan.release.cycle name=HistoryVulcan msg=<说明>。";

    internal const string ModuleUseStart =
        "模块请用 HistoryVulcan.Cli.exe --cli vulcan.dev.start。开发管线不走 MCP。";

    internal const string ModuleUseFinish =
        "模块请用 HistoryVulcan.Cli.exe --cli vulcan.dev.finish。开发管线不走 MCP。";

    internal const string ModuleUseSubmitFinish =
        "模块请用 HistoryVulcan.Cli.exe --cli vulcan.dev.submit / vulcan.dev.finish。本条只打包宿主：HistoryVulcan.Cli.exe --cli vulcan.release.cycle name=HistoryVulcan msg=<说明>。";

    internal const string MoveRootHint =
        "【迁根】Cursor 若有 move_agent_to_root（含 grok），现在把对话根迁进此工作区；其他 AI 不要迁根，按返回的绝对路径改文件。";

    internal const string MoveRootBeforeFinish =
        "【迁根】Cursor/grok 若对话根就是正在 finish 的工作区，先迁到该模块 Clio 主树再调用。其他 AI 不迁根：对话根不是那条工作区就可以 finish；对话根就是那条路径则禁止 finish（换对话，或有迁根工具则先迁走）。不要因为对话根在另一条 F 盘残留目录就停住。";

    /// <summary>CLI 可执行文件名，回执里给出可直接复制的命令时用。</summary>
    private const string CliExe = "HistoryVulcan.Cli.exe";

    public static void Register(CommandRegistry registry, DevelopmentContext host)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(host);

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.dev.start",
            HiddenReason = "开发管线不配 MCP，请用 HistoryVulcan.Cli.exe --cli vulcan.dev.start。",
            Domain = "vulcan",
            CommandClass = "dev",
            Summary = "模块开发第 1 步：开分支和工作区（宿主不走本管线）",
            Example = "vulcan.dev.start project=2026-020-HistoryJanus slug=cachekey agent=grok",
            Parameters =
            [
                Text("project", "项目目录名，例如 2026-020-HistoryJanus", required: true, position: 0),
                Text("slug", "本工作区要解决的问题，短标识，只用小写字母数字和连字符", required: true, position: 1),
                Text("agent", "开这个工作区的 AI 名字，例如 claude / grok", required: true, position: 2),
                Text("root", "本次使用的工作区根，省略时用设置值"),
                Bool("confirm", "项目已有闲置工作区时，仍坚持再开一个", "false"),
            ],
            Handler = CommandDescriptor.Sync(context => Start(
                host,
                context.RequireString("project"),
                context.RequireString("slug"),
                context.RequireString("agent"),
                context.GetString("root"),
                context.GetBool("confirm"))),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.dev.submit",
            HiddenReason = "开发管线不配 MCP，请用 HistoryVulcan.Cli.exe --cli vulcan.dev.submit。",
            Domain = "vulcan",
            CommandClass = "dev",
            Summary = "模块开发第 2 步：发布到 z、提交，并注册到宿主供审核",
            Example = "vulcan.dev.submit worktree=abc-1-grok-cachekey",
            Parameters =
            [
                Text("name", "已登记的模块名；省略时由工作区所在项目反查，写了就必须与之一致", position: 0),
                Text("msg", "提交说明；省略时取工作区名里的 slug", position: 1),
                Text("worktree", "vulcan.dev.start 返回的工作区目录名或绝对路径", required: true, position: 2),
                Bool("allowDirty", "（5.7.0 起无作用）工作区里未提交的改动正是 submit 要提交的，不再需要授权", "false"),
                Bool("dryRun", "只预检，不写文件、不构建、不提交、不热重载", "false"),
            ],
            Handler = async context => await SubmitAsync(
                host,
                context.GetString("name"),
                context.GetString("msg"),
                context.RequireString("worktree"),
                context.GetBool("dryRun"),
                context.Progress,
                context.Cancellation).ConfigureAwait(false),
        });

        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.dev.finish",
            HiddenReason = "开发管线不配 MCP，请用 HistoryVulcan.Cli.exe --cli vulcan.dev.finish。",
            Domain = "vulcan",
            CommandClass = "dev",
            Summary = "模块开发第 3 步（审核通过后）：复核已提交候选、并回 main、回收工作区并热装",
            Example = "vulcan.dev.finish worktree=abc-1-grok-cachekey",
            Parameters =
            [
                Text("name", "已登记的模块名；省略时由工作区所在项目反查，写了就必须与之一致", position: 0),
                Text("msg", "（5.7.0 起忽略）finish 是快进合并，不产生提交", position: 1),
                Text("worktree", "工作区目录名或绝对路径", required: true, position: 2),
            ],
            Handler = async context => await FinishAsync(
                host,
                context.GetString("name"),
                context.GetString("msg"),
                context.RequireString("worktree"),
                context.Progress,
                context.Cancellation).ConfigureAwait(false),
        });
    }

    internal static bool IsHostTarget(string nameOrProject)
        => nameOrProject.Equals("HistoryVulcan", StringComparison.OrdinalIgnoreCase)
           || nameOrProject.Equals("2026-023-HistoryVulcan", StringComparison.OrdinalIgnoreCase);

    internal static bool IsHostProject(ISettingsService settings, string nameOrProject)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(nameOrProject))
            return false;
        if (IsHostTarget(nameOrProject.Trim()))
            return true;
        if (ReleaseCommands.TryResolveModule(settings, nameOrProject.Trim(), out var byName)
            && byName.Kind.Equals("host", StringComparison.OrdinalIgnoreCase))
            return true;
        return ReleaseCommands.TryResolveModuleByProject(settings, nameOrProject.Trim(), out var byProject)
               && byProject.Kind.Equals("host", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 迁根提示只对能切对话根的 AI 有意义（5.7.0，U4）。其他 AI 每条回执都读一整段只是噪声。
    /// </summary>
    internal static bool MovesConversationRoot(string? agent)
        => agent?.Trim().ToLowerInvariant() is "cursor" or "grok";

    /// <summary>
    /// 拆工作区名 <c>&lt;短SHA&gt;-&lt;序号&gt;-&lt;agent&gt;-&lt;slug&gt;</c>（见 <see cref="WorktreeCommands.TryAllocateName"/>）。
    /// slug 本身可以带连字符，因此只切前三刀。
    /// </summary>
    internal static bool TryParseWorktreeName(string? name, out string agent, out string slug)
    {
        agent = "";
        slug = "";
        var parts = (name ?? "").Trim().Split('-', 4);
        if (parts.Length != 4 || !int.TryParse(parts[1], out _) || parts[2].Length == 0 || parts[3].Length == 0)
            return false;
        agent = parts[2];
        slug = parts[3];
        return true;
    }

    /// <summary>
    /// 由工作区反查它属于哪个项目（5.7.0，U1）。
    ///
    /// 绝对路径：问 git 这条工作树的公共 .git 在哪，项目就是它的上级目录。
    /// 目录名：在工作区根下找唯一一个含这个子目录的项目目录——start 就是按「根/项目/名」放的。
    /// 找到多个时不猜。
    /// </summary>
    internal static bool TryResolveWorktreeProject(
        ISettingsService settings, string worktree, out string project, out string path, out string error)
    {
        project = "";
        path = "";
        error = "";
        var value = worktree.Trim();
        if (value.Length == 0)
        {
            error = "worktree 不能为空。";
            return false;
        }

        if (Path.IsPathFullyQualified(value))
        {
            if (!Directory.Exists(value))
            {
                error = $"工作区不存在：{value}";
                return false;
            }

            path = value;
            var (common, commonError) = ToolProcess.Git(value, "rev-parse", "--path-format=absolute", "--git-common-dir");
            var gitDirectory = commonError == null && common.Length > 0
                ? Path.GetFullPath(common.Trim().TrimEnd('/', '\\'))
                : null;
            project = gitDirectory != null
                ? Path.GetFileName(Path.GetDirectoryName(gitDirectory) ?? "")
                : Path.GetFileName(Path.GetDirectoryName(value.TrimEnd('/', '\\')) ?? "");
            if (project.Length == 0)
            {
                error = $"读不出工作区 {value} 属于哪个项目。";
                return false;
            }

            return true;
        }

        var root = WorktreeCommands.ResolveRoot(settings, null);
        var matches = Directory.Exists(root)
            ? Directory.GetDirectories(root).Where(directory => Directory.Exists(Path.Combine(directory, value))).ToList()
            : [];
        if (matches.Count == 1)
        {
            project = Path.GetFileName(matches[0]);
            path = Path.Combine(matches[0], value);
            return true;
        }

        error = matches.Count == 0
            ? $"在工作区根 {root} 下找不到工作区 {value}。传绝对路径，或用 name= 指明模块。"
            : $"工作区名 {value} 在多个项目下都有：{string.Join("、", matches.Select(Path.GetFileName))}。传绝对路径。";
        return false;
    }

    /// <summary>
    /// submit / finish 的目标解析（5.7.0，U1）：<c>name</c> 可省略，由工作区项目推出；
    /// 写了而与工作区不一致时拒绝，并把两个值都说出来——不替人选。
    /// </summary>
    internal static bool TryResolveTarget(
        ISettingsService settings,
        string? name,
        string worktree,
        out ReleaseTarget module,
        out string worktreePath,
        out string error)
    {
        module = default!;
        worktreePath = "";
        error = "";
        var explicitName = name?.Trim() ?? "";
        if (explicitName.Length > 0 && IsHostProject(settings, explicitName))
        {
            error = HostRejected;
            return false;
        }

        var inferred = TryResolveWorktreeProject(settings, worktree, out var project, out var path, out var inferError);
        if (inferred)
        {
            if (IsHostProject(settings, project))
            {
                error = HostRejected;
                return false;
            }

            if (!ReleaseCommands.TryResolveModuleByProject(settings, project, out var byProject))
            {
                error = $"工作区 {worktree.Trim()} 所在项目 {project} 不在发布登记表里，见 vulcan.release.modules。";
                return false;
            }

            if (explicitName.Length > 0 && !explicitName.Equals(byProject.Name, StringComparison.OrdinalIgnoreCase))
            {
                error = $"name={explicitName} 与工作区不一致：工作区 {worktree.Trim()} 属于项目 {project}，"
                        + $"登记的模块是 {byProject.Name}。去掉 name=，或换成对应的工作区。";
                return false;
            }

            module = byProject;
            worktreePath = path;
            return true;
        }

        // 反查不到时，写了 name= 就照旧按「模块项目 + 工作区名」去找（工作区可能开在 root= 指定的别处）。
        if (explicitName.Length == 0)
        {
            error = inferError;
            return false;
        }

        if (!ReleaseCommands.TryResolveModule(settings, explicitName, out var byName)
            || byName.Kind.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            error = $"{explicitName} 不在发布登记表里，见 vulcan.release.modules。";
            return false;
        }

        module = byName;
        worktreePath = WorktreeCommands.ResolveWorktreePath(settings, byName.ProjectDirectory, worktree);
        return true;
    }

    /// <summary>
    /// 主树上的未提交改动（5.7.0，U5）。它们不在工作区里，也不会进本次提交；
    /// 直接在主树上改代码的人要到 finish 才发现并不回去，所以在 start 与 submit 就说出来。只警告，不拦。
    /// </summary>
    internal static string MainTreeWarning(string projectPath, string formalDirectory)
    {
        if (!Directory.Exists(projectPath))
            return "";
        string status;
        try
        {
            status = ToolProcess.CaptureLines(
                "git", ["status", "--porcelain", "--", $":!{formalDirectory}/**"], projectPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException
                                   or System.ComponentModel.Win32Exception or TimeoutException)
        {
            return "";
        }

        var lines = status.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 3)
            .ToList();
        if (lines.Count == 0)
            return "";

        var text = new StringBuilder(
            $"注意：主树 {projectPath} 有 {lines.Count} 个未提交文件，它们不在工作区里，也不会进本次提交：");
        foreach (var line in lines.Take(5))
            text.Append("\n  ").Append(line[3..]);
        if (lines.Count > 5)
            text.Append($"\n  …另有 {lines.Count - 5} 个");
        return text.ToString();
    }

    private static string MainProjectPath(ISettingsService settings, string projectDirectory)
        => Path.Combine(ProjectLibraryRoot.Resolve(settings), projectDirectory);

    private static CommandResult Start(
        DevelopmentContext host,
        string project,
        string slug,
        string agent,
        string? root,
        bool confirm)
    {
        if (IsHostProject(host.Settings, project))
            return CommandResult.Fail(HostRejected);

        var created = WorktreeCommands.Create(
            host.Settings, project, slug, agent, root, confirm);
        if (!created.Success)
            return created;

        var text = new StringBuilder(created.Message);
        if (MovesConversationRoot(agent))
            text.Append('\n').Append(MoveRootHint);

        var formal = ReleaseCommands.TryResolveModuleByProject(host.Settings, project.Trim(), out var module)
            ? module.FormalDirectory
            : "z-Publish";
        var warning = MainTreeWarning(MainProjectPath(host.Settings, project.Trim()), formal);
        if (warning.Length > 0)
            text.Append('\n').Append(warning);

        // 5.7.0（U6）：给出填好工作区的命令，照抄即可。自定义 root= 开在别处时给绝对路径（目录名反查不到）。
        var reference = ReadCreatedReference(created.Data, root);
        text.Append("\n改完后送审（未通过就继续改、再 submit）：")
            .Append($"\n  {CliExe} --cli \"vulcan.dev.submit worktree={reference}\"")
            .Append("\n审核通过后：")
            .Append($"\n  {CliExe} --cli \"vulcan.dev.finish worktree={reference}\"");
        return CommandResult.Ok(text.ToString(), created.Data);
    }

    private static string ReadCreatedReference(object? data, string? root)
    {
        try
        {
            var element = JsonSerializer.SerializeToElement(data);
            var name = element.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            var path = element.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "";
            return string.IsNullOrWhiteSpace(root) || path.Length == 0 ? name : CommandParser.QuoteArg(path);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return "<工作区名>";
        }
    }

    private static async Task<CommandResult> SubmitAsync(
        DevelopmentContext host,
        string? name,
        string? message,
        string worktree,
        bool dryRun,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (!TryResolveTarget(host.Settings, name, worktree, out var module, out var worktreePath, out var error))
            return CommandResult.Fail(error);

        var worktreeName = Path.GetFileName(worktreePath.TrimEnd('/', '\\'));
        TryParseWorktreeName(worktreeName, out var agent, out var slug);
        var commitMessage = string.IsNullOrWhiteSpace(message) ? slug : message.Trim();
        if (commitMessage.Length == 0)
            return CommandResult.Fail($"工作区名 {worktreeName} 里取不出 slug 作提交说明，请传 msg=。");

        var warning = MainTreeWarning(MainProjectPath(host.Settings, module.ProjectDirectory), module.FormalDirectory);

        // 5.7.0（U2）：dev 工作区是 start 从干净的 HEAD 开出来的，里面的改动全是本轮要提交的，
        // 不再要求 allowDirty。主树上的 vulcan.release.cycle 仍保留这道门。
        var result = await ReleaseCommands.CycleAsync(
            host, module.Name, commitMessage, worktreePath, allowDirty: true, dryRun, progress, cancellation)
            .ConfigureAwait(false);

        var text = new StringBuilder(result.Message);
        if (warning.Length > 0)
            text.Append('\n').Append(warning);
        if (!result.Success)
            return CommandResult.Fail(text.ToString());

        if (dryRun)
        {
            text.Append("\ndryRun：未登记审核，也没有提交。去掉 dryRun=true 正式送审。");
        }
        else
        {
            text.Append("\n已登记宿主审核。未通过：改代码后再 vulcan.dev.submit。")
                .Append($"\n通过后：{CliExe} --cli \"vulcan.dev.finish worktree={worktreeName}\"");
            if (MovesConversationRoot(agent))
                text.Append('\n').Append(MoveRootBeforeFinish);
        }

        return new CommandResult { Success = true, Message = text.ToString(), Data = result.Data };
    }

    private static async Task<CommandResult> FinishAsync(
        DevelopmentContext host,
        string? name,
        string? message,
        string worktree,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (!TryResolveTarget(host.Settings, name, worktree, out var module, out var worktreePath, out var error))
            return CommandResult.Fail(error);

        var worktreeName = Path.GetFileName(worktreePath.TrimEnd('/', '\\'));
        TryParseWorktreeName(worktreeName, out var agent, out _);
        if (MovesConversationRoot(agent))
            progress?.Report(MoveRootBeforeFinish);

        var status = ToolProcess.CaptureLines("git", ["status", "--porcelain"], worktreePath);
        if (status.Length > 0)
            return CommandResult.Fail($"工作树不干净，finish 只能复用已提交的 submit 候选。\n{status}");

        var published = ReleaseCommands.VerifySubmittedCandidate(host, module.Name, worktreePath);
        if (!published.Success)
            return published;

        var merged = await WorktreeCommands.MergeAsync(
            host, module.ProjectDirectory, worktreeName, cancellation).ConfigureAwait(false);

        var text = new StringBuilder(published.Message + "\n" + merged.Message);
        if (!string.IsNullOrWhiteSpace(message))
            text.Append("\nfinish 是快进合并、不产生提交，msg 已忽略。");
        if (MovesConversationRoot(agent))
            text.Append('\n').Append(MoveRootBeforeFinish);
        return merged.Success
            ? CommandResult.Ok(text.ToString(), new { Submit = published.Data, Merge = merged.Data })
            : CommandResult.Fail(text.ToString());
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
}
