using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using System.Text;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Mcp;

/// <summary>一个 MCP 工具的描述治理现状。</summary>
/// <param name="ToolName">MCP 工具名。</param>
/// <param name="CommandName">对应的完整指令名。</param>
/// <param name="DefaultDescription">代码内置的默认描述。</param>
/// <param name="EffectiveDescription">当前实际生效的描述。</param>
/// <param name="CurrentRevision">当前生效的修订；未被修订过为 null。</param>
/// <param name="OpenProposals">待审核的提案数。</param>
public sealed record PromptStatus(
    string ToolName,
    string CommandName,
    string DefaultDescription,
    string EffectiveDescription,
    PromptRevision? CurrentRevision,
    int OpenProposals);

/// <summary>一份提案及其与生效描述的文本差异。</summary>
/// <param name="Proposal">提案本体。</param>
/// <param name="Diff">相对生效描述的差异文本。</param>
public sealed record PromptProposalDiff(PromptProposal Proposal, string Diff);

/// <summary>V2.1.2 提示词修订、提案、勘误和事故指令。</summary>
public static class PromptGovernanceCommands
{
    /// <summary>把提示词治理指令注册进指定注册表。</summary>
    public static void RegisterAll(
        CommandRegistry registry, CommandSchemaExporter exporter, PromptGovernanceStore store,
        string source = "app")
    {
        registry.Register(BuildLegacyDescription(exporter, store), source);
        registry.Register(BuildGet(exporter, store), source);
        registry.Register(BuildHistory(exporter, store), source);
        registry.Register(BuildDiff(store), source);
        registry.Register(BuildPropose(exporter, store), source);
        registry.Register(BuildCorrectionList(store), source);
        registry.Register(BuildCorrectionPropose(exporter, store), source);
        registry.Register(BuildIncidentList(store), source);
        registry.Register(BuildIncidentRecord(exporter, store), source);
        registry.Register(BuildPending(store), source);
        registry.Register(BuildApprove(store), source);
        registry.Register(BuildReject(store), source);
        registry.Register(BuildApply(store), source);
        registry.Register(BuildRevert(store), source);
    }

    private static CommandDescriptor BuildLegacyDescription(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.mcp.desc",
            Domain = "vulcan",
            CommandClass = "mcp",
            Summary = "本地查看/直接修订 MCP 工具描述；远程 AI 请使用 vulcan.prompt.propose",
            Example = "vulcan.mcp.desc name=vulcan.command.list text=\"列出当前可用指令\" reason=人工修订",
            Parameters =
        [
            StringParam("name", "指令名或工具名", required: true, position: 0),
            StringParam("text", "新描述；省略只查看", position: 1),
            StringParam("reason", "修订理由", defaultValue: "本地直接修订"),
            StringParam("reviewer", "执行人；省略使用当前 Windows 用户"),
            BoolParam("reset", "恢复指令自带描述", "false"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tool = RequireTool(exporter, ctx.RequireString("name"));
                if (ctx.GetBool("reset"))
                {
                    var revision = store.ApplyDirect(
                        tool.CommandName, null, ctx.Source, ctx.GetString("reason") ?? "恢复默认描述",
                        createdBy: Reviewer(ctx));
                    return CommandResult.Ok(
                        $"已恢复默认描述: {tool.CommandName}\n修订: {revision.Id}\n{tool.DefaultDescription}", revision);
                }

                var text = ctx.GetString("text");
                if (string.IsNullOrWhiteSpace(text))
                    return CommandResult.Ok(FormatStatus(BuildStatus(tool, store)), BuildStatus(tool, store));

                try
                {
                    text = ValidateDescription(text);
                }
                catch (InvalidOperationException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
                var applied = store.ApplyDirect(
                    tool.CommandName, text, ctx.Source, ctx.GetString("reason") ?? "本地直接修订",
                    createdBy: Reviewer(ctx));
                return CommandResult.Ok(
                    $"已应用本地修订: {tool.CommandName}\n修订: {applied.Id}\n客户端下次 tools/list 生效",
                    applied);
            }),
        };

    private static CommandDescriptor BuildGet(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.prompt.get",
            Domain = "vulcan",
            CommandClass = "prompt",
            Summary = "查看 MCP 工具的默认描述、生效描述、当前修订和待审核提案数",
            Readonly = true,
            Example = "vulcan.prompt.get name=vulcan.command.list",
            Parameters = [StringParam("name", "指令名或工具名", required: true, position: 0)],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name").Trim();
                var tool = exporter.Find(name);
                if (tool == null)
                    return CommandResult.Ok(NoLocalProjection(name), null);

                var status = BuildStatus(tool, store);
                return CommandResult.Ok(FormatStatus(status), status);
            }),
        };

    private static CommandDescriptor BuildHistory(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.prompt.history",
            Domain = "vulcan",
            CommandClass = "prompt",
            Summary = "查看某个 MCP 工具的描述修订历史",
            Readonly = true,
            Example = "vulcan.prompt.history name=vulcan.command.list limit=20",
            Parameters =
        [
            StringParam("name", "指令名或工具名", required: true, position: 0),
            IntParam("limit", "最多返回条数", "20"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name").Trim();
                var tool = exporter.Find(name);
                if (tool == null)
                    return CommandResult.Ok(NoLocalProjection(name), Array.Empty<PromptRevision>());

                var rows = store.GetRevisions(tool.CommandName, ctx.GetInt("limit", 20));
                if (rows.Count == 0)
                    return CommandResult.Ok($"{tool.CommandName} 尚无修订，当前使用指令默认描述", rows);

                var sb = new StringBuilder($"{tool.CommandName} 修订历史({rows.Count}):");
                foreach (var row in rows)
                {
                    sb.Append($"\n  {row.Id}  {row.Created}  {row.Source}/{row.CreatedBy}");
                    if (row.Applied)
                        sb.Append("  [当前]");
                    if (row.Description == null)
                        sb.Append("  [默认描述]");
                    sb.Append($"\n    {FirstLine(row.Description ?? tool.DefaultDescription)}");
                }
                return CommandResult.Ok(sb.ToString(), rows);
            }),
        };

    private static CommandDescriptor BuildDiff(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.prompt.diff",
        Domain = "vulcan",
        CommandClass = "prompt",
        Summary = "查看提示词提案的原文、新文和文本差异",
        Readonly = true,
        Example = "vulcan.prompt.diff id=proposal_xxx",
        Parameters = [StringParam("id", "提案 ID", required: true, position: 0)],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var proposal = store.GetProposal(ctx.RequireString("id"))
                           ?? throw new InvalidOperationException("提案不存在");
            var diff = BuildTextDiff(proposal.OldText, proposal.ProposedText);
            return CommandResult.Ok(
                $"提案 {proposal.Id} [{proposal.Status}] {proposal.Command}\n理由: {proposal.Reason}\n{diff}",
                new PromptProposalDiff(proposal, diff));
        }),
    };

    private static CommandDescriptor BuildPropose(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.prompt.propose",
            Domain = "vulcan",
            CommandClass = "prompt",
            Summary = "提交 MCP 工具描述修改提案；不会直接改变生效描述",
            Example = "vulcan.prompt.propose name=vulcan.command.list text=\"列出当前可用指令\" reason=澄清目录语义",
            Parameters =
        [
            StringParam("name", "指令名或工具名", required: true, position: 0),
            StringParam("text", "建议的新描述", required: true, position: 1),
            StringParam("reason", "修改理由", required: true),
            StringParam("evidence", "证据、错误现场或引用"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tool = RequireTool(exporter, ctx.RequireString("name"));
                string proposed;
                try
                {
                    proposed = ValidateDescription(ctx.RequireString("text"));
                }
                catch (InvalidOperationException ex)
                {
                    return CommandResult.Fail(ex.Message);
                }
                if (string.Equals(tool.Description, proposed, StringComparison.Ordinal))
                    return CommandResult.Fail("提案内容与当前生效描述完全相同");

                var proposal = store.CreateProposal(
                    tool.CommandName, tool.Description, proposed,
                    RequiredTrimmed(ctx, "reason", 1000), OptionalTrimmed(ctx, "evidence", 4000), ctx.Source);
                return CommandResult.Ok(
                    $"已提交待审核提案: {proposal.Id}\n{proposal.Command}\n当前描述未改变",
                    proposal);
            }),
        };

    private static CommandDescriptor BuildCorrectionList(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.prompt.corrections",
        Domain = "vulcan",
        CommandClass = "prompt",
        Summary = "列出 MCP 工具描述勘误记录",
        Readonly = true,
        Example = "vulcan.prompt.corrections name=vulcan.command.list limit=20",
        Parameters =
        [
            StringParam("name", "可选指令名"),
            IntParam("limit", "最多返回条数", "50"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var rows = store.ListCorrections(ctx.GetString("name")?.Trim(), ctx.GetInt("limit", 50));
            var text = rows.Count == 0
                ? "(无勘误记录)"
                : "勘误记录:" + string.Concat(rows.Select(r =>
                    $"\n  {r.Id} [{r.Status}] {r.Command} {r.Created}\n    {FirstLine(r.Correction)}"));
            return CommandResult.Ok(text, rows);
        }),
    };

    private static CommandDescriptor BuildCorrectionPropose(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.prompt.correct",
            Domain = "vulcan",
            CommandClass = "prompt",
            Summary = "提交工具描述勘误，不直接修改生效描述",
            Example = "vulcan.prompt.correct name=vulcan.command.list claim=\"只返回框架指令\" correction=\"返回权威目录中的全部指令\"",
            Parameters =
        [
            StringParam("name", "指令名或工具名", required: true, position: 0),
            StringParam("claim", "需要纠正的原说法", required: true),
            StringParam("correction", "正确解释", required: true),
            StringParam("evidence", "证据或复现记录"),
            StringParam("proposal", "可选关联的提示词提案 ID"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tool = RequireTool(exporter, ctx.RequireString("name"));
                var linkedProposal = ctx.GetString("proposal")?.Trim();
                if (!string.IsNullOrEmpty(linkedProposal) && store.GetProposal(linkedProposal) == null)
                    return CommandResult.Fail($"关联提案不存在: {linkedProposal}");
                var item = store.CreateCorrection(
                    tool.CommandName, RequiredTrimmed(ctx, "claim", 2000),
                    RequiredTrimmed(ctx, "correction", 2000), OptionalTrimmed(ctx, "evidence", 4000), ctx.Source,
                    linkedProposal);
                return CommandResult.Ok($"已记录待处理勘误: {item.Id}", item);
            }),
        };

    private static CommandDescriptor BuildIncidentList(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.prompt.incidents",
        Domain = "vulcan",
        CommandClass = "prompt",
        Summary = "列出 MCP 工具调用或描述事故记录",
        Readonly = true,
        Example = "vulcan.prompt.incidents name=vulcan.command.list limit=20",
        Parameters =
        [
            StringParam("name", "可选指令名"),
            IntParam("limit", "最多返回条数", "50"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var rows = store.ListIncidents(ctx.GetString("name")?.Trim(), ctx.GetInt("limit", 50));
            var text = rows.Count == 0
                ? "(无事故记录)"
                : "事故记录:" + string.Concat(rows.Select(r =>
                    $"\n  {r.Id} {r.Command} {r.Created}\n    {FirstLine(r.Symptom)}"));
            return CommandResult.Ok(text, rows);
        }),
    };

    private static CommandDescriptor BuildIncidentRecord(
        CommandSchemaExporter exporter, PromptGovernanceStore store) => new()
        {
            Name = "vulcan.prompt.record",
            Domain = "vulcan",
            CommandClass = "prompt",
            Summary = "记录工具调用或描述事故；保留预期、实际和证据",
            Example = "vulcan.prompt.record name=vulcan.command.list symptom=遗漏前端指令 expected=返回权威目录 actual=只返回后端指令",
            Parameters =
        [
            StringParam("name", "指令名或工具名", required: true, position: 0),
            StringParam("symptom", "问题现象", required: true),
            StringParam("expected", "预期行为", required: true),
            StringParam("actual", "实际行为", required: true),
            StringParam("evidence", "日志、复现步骤或引用"),
            StringParam("correction", "可选关联的勘误 ID"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var tool = RequireTool(exporter, ctx.RequireString("name"));
                var linkedCorrection = ctx.GetString("correction")?.Trim();
                if (!string.IsNullOrEmpty(linkedCorrection) && store.GetCorrection(linkedCorrection) == null)
                    return CommandResult.Fail($"关联勘误不存在: {linkedCorrection}");
                var item = store.CreateIncident(
                    tool.CommandName, RequiredTrimmed(ctx, "symptom", 2000),
                    RequiredTrimmed(ctx, "expected", 2000), RequiredTrimmed(ctx, "actual", 2000),
                    OptionalTrimmed(ctx, "evidence", 4000), ctx.Source, linkedCorrection);
                return CommandResult.Ok($"已记录事故: {item.Id}", item);
            }),
        };

    private static CommandDescriptor BuildPending(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.mcp.pending",
        Domain = "vulcan",
        CommandClass = "mcp",
        Summary = "本地列出待审核或已批准未应用的提示词提案",
        Example = "vulcan.mcp.pending name=vulcan.command.list",
        Parameters =
        [
            StringParam("name", "可选指令名"),
            IntParam("limit", "最多返回条数", "100"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var rows = store.ListProposals(ctx.GetString("name")?.Trim(), openOnly: true, ctx.GetInt("limit", 100));
            var text = rows.Count == 0
                ? "(无待审核提案)"
                : "待处理提案:" + string.Concat(rows.Select(r =>
                    $"\n  {r.Id} [{r.Status}] {r.Command} {r.Created}\n    {FirstLine(r.ProposedText)}"));
            return CommandResult.Ok(text, rows);
        }),
    };

    private static CommandDescriptor BuildApprove(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.mcp.approve",
        Domain = "vulcan",
        CommandClass = "mcp",
        Summary = "本地批准提示词提案；批准后仍需 apply 才生效",
        Example = "vulcan.mcp.approve id=proposal_xxx reviewer=Administrator",
        Parameters =
        [
            StringParam("id", "提案 ID", required: true, position: 0),
            StringParam("reviewer", "审核人；省略使用当前 Windows 用户"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var item = store.ApproveProposal(ctx.RequireString("id"), Reviewer(ctx));
            return CommandResult.Ok($"已批准提案: {item.Id}\n尚未应用，当前描述不变", item);
        }),
    };

    private static CommandDescriptor BuildReject(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.mcp.reject",
        Domain = "vulcan",
        CommandClass = "mcp",
        Summary = "本地拒绝提示词提案并保留理由",
        Example = "vulcan.mcp.reject id=proposal_xxx reason=边界描述不准确",
        Parameters =
        [
            StringParam("id", "提案 ID", required: true, position: 0),
            StringParam("reason", "拒绝理由", required: true),
            StringParam("reviewer", "审核人；省略使用当前 Windows 用户"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var item = store.RejectProposal(
                ctx.RequireString("id"), Reviewer(ctx), RequiredTrimmed(ctx, "reason", 1000));
            return CommandResult.Ok($"已拒绝提案: {item.Id}", item);
        }),
    };

    private static CommandDescriptor BuildApply(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.mcp.apply",
        Domain = "vulcan",
        CommandClass = "mcp",
        Summary = "本地应用已批准提案；基线变化时拒绝覆盖",
        Example = "vulcan.mcp.apply id=proposal_xxx reviewer=Administrator",
        Parameters =
        [
            StringParam("id", "提案 ID", required: true, position: 0),
            StringParam("reviewer", "应用人；省略使用当前 Windows 用户"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var revision = store.ApplyProposal(ctx.RequireString("id"), Reviewer(ctx));
            return CommandResult.Ok(
                $"已应用提案，新修订: {revision.Id}\n客户端下次 tools/list 生效", revision);
        }),
    };

    private static CommandDescriptor BuildRevert(PromptGovernanceStore store) => new()
    {
        Name = "vulcan.mcp.revert",
        Domain = "vulcan",
        CommandClass = "mcp",
        Summary = "本地把指定历史修订内容生成为新的当前修订",
        Example = "vulcan.mcp.revert revision=rev_xxx reason=回退错误描述",
        Parameters =
        [
            StringParam("revision", "目标历史修订 ID", required: true, position: 0),
            StringParam("reason", "回滚理由", required: true),
            StringParam("reviewer", "执行人；省略使用当前 Windows 用户"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var revision = store.RevertToRevision(
                ctx.RequireString("revision"), Reviewer(ctx), RequiredTrimmed(ctx, "reason", 1000));
            return CommandResult.Ok($"已生成回滚修订: {revision.Id}", revision);
        }),
    };

    private static PromptStatus BuildStatus(McpToolInfo tool, PromptGovernanceStore store)
        => new(
            tool.ToolName,
            tool.CommandName,
            tool.DefaultDescription,
            tool.Description,
            store.GetCurrentRevision(tool.CommandName),
            store.ListProposals(tool.CommandName, openOnly: true).Count);

    private static string FormatStatus(PromptStatus status)
        => $"{status.ToolName} ← {status.CommandName}" +
           $"\n默认描述: {status.DefaultDescription}" +
           $"\n生效描述: {status.EffectiveDescription}" +
           $"\n当前修订: {status.CurrentRevision?.Id ?? "(默认)"}" +
           $"\n待处理提案: {status.OpenProposals}";

    /// <summary>
    /// 只读查询在指令不在本地 MCP 投影里时的回答。
    /// </summary>
    /// <remarks>
    /// 提示词治理的投影建立在**本进程**注册表之上，而模块指令注册在服务进程，
    /// 因此 janus/mercury/diana 等模块指令在这里查不到。这不是错误：
    /// 它们只是没有本地治理记录。以前这里直接抛异常，命令集里点一条模块指令
    /// 就会弹红字——详情页对每条选中指令都会拉一次 get 和 history。
    /// 写指令（propose/apply 等）仍走 RequireTool 硬失败：改一条不存在的投影必须报错。
    /// </remarks>
    private static string NoLocalProjection(string name)
        => $"{name} 没有本地 MCP 投影，因而没有可治理的描述记录。"
           + "模块指令由所属模块进程注册，其描述在模块自己的合同里维护。";

    private static McpToolInfo RequireTool(CommandSchemaExporter exporter, string name)
        => exporter.Find(name.Trim())
           ?? throw new InvalidOperationException($"未找到 MCP 工具/指令: {name}");

    private static string ValidateDescription(string text)
        => PromptTextIntegrity.ValidateDescription(text);

    private static string RequiredTrimmed(CommandContext ctx, string name, int max)
    {
        var value = ctx.RequireString(name).Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"{name} 不能为空");
        if (value.Length > max)
            throw new InvalidOperationException($"{name} 过长({value.Length} 字符，上限 {max})");
        return value;
    }

    private static string OptionalTrimmed(CommandContext ctx, string name, int max)
    {
        var value = ctx.GetString(name)?.Trim() ?? "";
        if (value.Length > max)
            throw new InvalidOperationException($"{name} 过长({value.Length} 字符，上限 {max})");
        return value;
    }

    private static string Reviewer(CommandContext ctx)
        => string.IsNullOrWhiteSpace(ctx.GetString("reviewer"))
            ? Environment.UserName
            : ctx.GetString("reviewer")!.Trim();

    private static string BuildTextDiff(string oldText, string newText)
    {
        var oldLines = oldText.Replace("\r\n", "\n").Split('\n');
        var newLines = newText.Replace("\r\n", "\n").Split('\n');
        if (oldLines.SequenceEqual(newLines, StringComparer.Ordinal))
            return "(无差异)";

        return string.Join('\n', oldLines.Select(line => "- " + line)) + "\n" +
               string.Join('\n', newLines.Select(line => "+ " + line));
    }

    private static string FirstLine(string text)
        => text.Replace("\r\n", "\n").Split('\n')[0];

    private static ParameterSpec StringParam(
        string name, string description, bool required = false, int? position = null, string? defaultValue = null)
        => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
            Default = defaultValue,
        };

    private static ParameterSpec IntParam(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Int,
            Default = defaultValue,
        };

    private static ParameterSpec BoolParam(string name, string description, string defaultValue)
        => new()
        {
            Name = name,
            Description = description,
            Type = ParamType.Bool,
            Default = defaultValue,
        };
}
