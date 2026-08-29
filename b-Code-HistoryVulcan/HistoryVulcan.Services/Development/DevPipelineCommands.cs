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
            Example = "vulcan.dev.submit name=HistoryJanus msg=fix-layout worktree=abc-1-grok-cachekey",
            Parameters =
            [
                Text("name", "已登记的模块名", required: true, position: 0),
                Text("msg", "提交说明", required: true, position: 1),
                Text("worktree", "vulcan.dev.start 返回的工作区目录名或绝对路径", required: true, position: 2),
                Bool("allowDirty", "允许从有未提交变更的工作树提交", "false"),
                Bool("dryRun", "只预检，不写文件、不构建、不提交、不热重载", "false"),
            ],
            Handler = async context => await SubmitAsync(
                host,
                context.RequireString("name"),
                context.RequireString("msg"),
                context.RequireString("worktree"),
                context.GetBool("allowDirty"),
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
            Summary = "模块开发第 3 步（审核通过后）：再发 z、提交、并回 main、删工作区、注册到宿主",
            Example = "vulcan.dev.finish name=HistoryJanus msg=ship worktree=abc-1-grok-cachekey",
            Parameters =
            [
                Text("name", "已登记的模块名", required: true, position: 0),
                Text("msg", "提交说明", required: true, position: 1),
                Text("worktree", "工作区目录名或绝对路径", required: true, position: 2),
            ],
            Handler = async context => await FinishAsync(
                host,
                context.RequireString("name"),
                context.RequireString("msg"),
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

        return CommandResult.Ok(
            created.Message + "\n" + MoveRootHint + "\n改完后调用 vulcan.dev.submit。未通过审核就继续改、再 submit。",
            created.Data);
    }

    private static async Task<CommandResult> SubmitAsync(
        DevelopmentContext host,
        string name,
        string message,
        string worktree,
        bool allowDirty,
        bool dryRun,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (IsHostProject(host.Settings, name))
            return CommandResult.Fail(HostRejected);

        var result = await ReleaseCommands.CycleAsync(
            host, name, message, worktree, allowDirty, dryRun, progress, cancellation).ConfigureAwait(false);
        if (!result.Success)
            return result;

        return CommandResult.Ok(
            result.Message
            + "\n已注册到宿主审核。未通过：改代码后再 vulcan.dev.submit。"
            + "\n通过后：Cursor/grok 先按需迁出工作区，再 vulcan.dev.finish。",
            result.Data);
    }

    private static async Task<CommandResult> FinishAsync(
        DevelopmentContext host,
        string name,
        string message,
        string worktree,
        IProgress<string>? progress,
        CancellationToken cancellation)
    {
        if (IsHostProject(host.Settings, name))
            return CommandResult.Fail(HostRejected);

        if (!ReleaseCommands.TryResolveModule(host.Settings, name.Trim(), out var module)
            || module.Kind.Equals("host", StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail($"{name.Trim()} 不在发布登记表里。");
        }

        progress?.Report(MoveRootBeforeFinish);
        var worktreePath = WorktreeCommands.ResolveWorktreePath(
            host.Settings, module.ProjectDirectory, worktree);
        var status = ToolProcess.Capture("git", ["status", "--porcelain"], worktreePath);
        if (status.Length > 0)
            return CommandResult.Fail($"工作树不干净，finish 只能复用已提交的 submit 候选。\n{status}");

        var published = ReleaseCommands.VerifySubmittedCandidate(host, module.Name, worktreePath);
        if (!published.Success)
            return published;

        var worktreeName = Directory.Exists(worktreePath)
            ? Path.GetFileName(worktreePath)
            : worktree.Trim();
        var merged = await WorktreeCommands.MergeAsync(
            host, module.ProjectDirectory, worktreeName, cancellation).ConfigureAwait(false);

        var text = published.Message + "\n" + merged.Message + "\n" + MoveRootBeforeFinish;
        return merged.Success
            ? CommandResult.Ok(text, new { Submit = published.Data, Merge = merged.Data })
            : CommandResult.Fail(text);
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
