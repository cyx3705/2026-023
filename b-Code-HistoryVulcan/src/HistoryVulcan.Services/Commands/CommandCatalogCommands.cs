using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using System.IO;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.Commands;

namespace HistoryVulcan.Services.Commands;

/// <summary>目录中的一行：一条指令的注册事实与 MCP 投影状态。</summary>
/// <param name="CommandName">完整指令名。</param>
/// <param name="Domain">所属指令域。</param>
/// <param name="Summary">一句话说明。</param>
/// <param name="Example">示例调用；无示例为 null。</param>
/// <param name="ParameterCount">参数个数。</param>
/// <param name="Source">注册方，如 framework:service 或 frontend:*。</param>
/// <param name="SourceDetail">注册方补充信息；无则为 null。</param>
/// <param name="Dangerous">级别是否为「询问」（执行前必须问过人）。</param>
/// <param name="RequiresUiThread">是否必须在 UI 线程执行。</param>
/// <param name="McpToolName">投影后的 MCP 工具名；未投影为 null。</param>
/// <param name="McpState">MCP 投影状态：readonly / standard / dangerous / hidden。</param>
/// <param name="PolicyVisible">按当前策略是否对远程可见。</param>
/// <param name="Customized">工具描述是否已被治理修订覆盖。</param>
/// <param name="CurrentRevision">当前生效的描述修订号；无则为 null。</param>
/// <param name="OpenProposals">待审核的描述提案数。</param>
/// <param name="IncidentCount">已记录的事故数。</param>
/// <param name="HardExclusionReason">硬排除原因；未被硬排除为 null。</param>
public sealed record CommandCatalogRow(
    string CommandName,
    string Domain,
    string Summary,
    string? Example,
    int ParameterCount,
    string Source,
    string? SourceDetail,
    bool Dangerous,
    bool RequiresUiThread,
    string? McpToolName,
    string McpState,
    bool PolicyVisible,
    bool Customized,
    string? CurrentRevision,
    int OpenProposals,
    int IncidentCount,
    string? HardExclusionReason)
{
    /// <summary>指令在所属域内的功能类；附加属性保持旧位置构造函数兼容。</summary>
    public string CommandClass { get; init; } = "core";

    /// <summary>三段式命令名的末段方法名。</summary>
    public string Method { get; init; } = "";
}

/// <summary>一条指令的单个参数说明。</summary>
/// <param name="Name">参数名。</param>
/// <param name="Type">参数类型。</param>
/// <param name="Required">是否必填。</param>
/// <param name="Default">默认值；无默认为 null。</param>
/// <param name="Position">位置参数序号；仅具名时为 null。</param>
/// <param name="AllowedValues">允许值枚举；不限时为空。</param>
/// <param name="Description">参数说明。</param>
public sealed record CommandParameterInfo(
    string Name,
    string Type,
    bool Required,
    string? Default,
    int? Position,
    IReadOnlyList<string> AllowedValues,
    string Description);

/// <summary>单条指令的完整详情：目录行、参数表与 MCP 输入 schema。</summary>
/// <param name="Command">该指令的目录行。</param>
/// <param name="Parameters">参数表。</param>
/// <param name="McpInputSchema">MCP 工具的输入 JSON Schema；未投影为 null。</param>
public sealed record CommandCatalogDetail(
    CommandCatalogRow Command,
    IReadOnlyList<CommandParameterInfo> Parameters,
    string? McpInputSchema)
{
    /// <summary>命令注册方提供、由具体消费方解释的目录注解。</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一个指令域及其注册数量。</summary>
/// <param name="Domain">域名。</param>
/// <param name="Count">该域下的指令数。</param>
public sealed record CommandDomainInfo(string Domain, int Count);

/// <summary>V2.1.3 全指令结构化目录，注册表是唯一上游。</summary>
public static class CommandCatalogCommands
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 注册不依赖提示词治理的核心目录指令，供只要命令目录、不承载 MCP 治理的宿主使用。
    ///
    /// 4.0.0 从 internal 提为 public：本类型随 REQ-A1 从 Shell 迁入 Services 后，其调用方
    /// （前端）将在 REQ-A3 成为独立仓的独立应用，跨程序集的 internal 不再可达；
    /// InternalsVisibleTo 绑定具体程序集名，对外部消费方不成立。
    /// </summary>
    public static void RegisterCore(CommandRegistry registry, string source = "app")
    {
        var exporter = new CommandSchemaExporter(registry);
        RegisterCatalog(registry, exporter, governance: null, static () => "readonly", source);
    }

    /// <summary>把目录查询指令注册进指定注册表。</summary>
    public static void RegisterAll(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        Func<string> policy,
        string source = "app")
        => RegisterCatalog(registry, exporter, governance, policy, source);

    private static void RegisterCatalog(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        Func<string> policy,
        string source)
    {
        registry.Register(BuildList(registry, exporter, governance, policy), source);
        registry.Register(BuildShow(registry, exporter, governance, policy), source);
        registry.Register(BuildDomains(registry), source);
        registry.Register(BuildManual(registry, exporter, policy), source);
    }

    /// <summary>按当前注册表与 MCP 投影生成目录快照。</summary>
    public static IReadOnlyList<CommandCatalogRow> Snapshot(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        string policy)
        => SnapshotCore(registry, exporter, governance, policy);

    private static IReadOnlyList<CommandCatalogRow> SnapshotCore(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        string policy)
    {
        var tools = exporter.ExportTools().ToDictionary(
            tool => tool.CommandName, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> descriptions = governance?.EffectiveDescriptions()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> openProposals = governance?.OpenProposalCounts()
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> incidents = governance?.IncidentCounts()
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        return registry.All().Select(descriptor =>
        {
            tools.TryGetValue(descriptor.Name, out var tool);
            var rawSource = registry.GetSource(descriptor.Name);
            var module = rawSource.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
            var sourceName = module ? "module" : rawSource;
            var sourceDetail = module ? rawSource["module:".Length..] : null;
            var customized = descriptions.ContainsKey(descriptor.Name);
            var revision = customized ? governance?.CurrentRevisionId(descriptor.Name) : null;

            return new CommandCatalogRow(
                descriptor.Name,
                registry.GetDomain(descriptor.Name),
                descriptor.Summary,
                descriptor.Example,
                descriptor.Parameters.Count,
                sourceName,
                sourceDetail,
                descriptor.Level == CommandLevel.Ask,
                descriptor.RequiresUiThread,
                tool?.ToolName,
                McpExposurePolicy.State(descriptor),
                McpExposurePolicy.IsVisible(descriptor, policy),
                customized,
                revision,
                openProposals.GetValueOrDefault(descriptor.Name),
                incidents.GetValueOrDefault(descriptor.Name),
                McpExposurePolicy.HardExclusionReason(descriptor))
            {
                CommandClass = registry.GetCommandClass(descriptor.Name),
                Method = CommandRegistry.GetMethod(descriptor.Name),
            };
        }).ToList();
    }

    private static CommandDescriptor BuildList(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        Func<string> policy) => new()
        {
            Name = "vulcan.command.list",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "结构化列出全部注册指令及其来源、风险和 MCP 投影",
            Readonly = true,
            Example = "vulcan.command.list domain=vulcan class=win mcp=visible filter=dock",
            Parameters =
        [
            StringParam("domain", "可选指令域，如 proj / attr / command"),
            StringParam("class", "可选域内命令类，如 win / log / module"),
            new ParameterSpec
            {
                Name = "mcp",
                Description = "按当前策略过滤 MCP 可见性",
                Default = "all",
                AllowedValues = ["all", "visible", "hidden"],
            },
            StringParam("filter", "按名称、说明或来源搜索"),
        ],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                IEnumerable<CommandCatalogRow> rows = SnapshotCore(
                    registry, exporter, governance, policy());
                var domain = ctx.GetString("domain")?.Trim();
                if (!string.IsNullOrWhiteSpace(domain))
                    rows = rows.Where(row => row.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

                var commandClass = ctx.GetString("class")?.Trim();
                if (!string.IsNullOrWhiteSpace(commandClass))
                    rows = rows.Where(row => row.CommandClass.Equals(
                        commandClass,
                        StringComparison.OrdinalIgnoreCase));

                var mcp = ctx.GetString("mcp") ?? "all";
                rows = mcp.ToLowerInvariant() switch
                {
                    "visible" => rows.Where(row => row.PolicyVisible),
                    "hidden" => rows.Where(row => !row.PolicyVisible),
                    _ => rows,
                };

                var filter = ctx.GetString("filter")?.Trim();
                if (!string.IsNullOrWhiteSpace(filter))
                {
                    rows = rows.Where(row =>
                        row.CommandName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || row.Summary.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || row.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)
                        || (row.SourceDetail?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
                }

                var list = rows.ToList();
                var text = new StringBuilder($"命令集: {list.Count} / {registry.All().Count} 条");
                foreach (var row in list)
                    text.Append($"\n  {row.CommandName,-28} "
                                + $"[{row.Domain}/{CommandClassLabels.Display(row.CommandClass)}/{row.McpState}] "
                                + row.Summary);
                return CommandResult.Ok(text.ToString(), list);
            }),
        };

    private static CommandDescriptor BuildShow(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        IMcpPromptGovernanceView? governance,
        Func<string> policy) => new()
        {
            Name = "vulcan.command.show",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "查看单条指令的 Help 参数、来源、风险和 MCP 映射",
            Readonly = true,
            Example = "vulcan.command.show name=vulcan.mcp.apply",
            Parameters = [StringParam("name", "完整指令名", required: true, position: 0)],
            Handler = CommandDescriptor.Sync(ctx =>
            {
                var name = ctx.RequireString("name").Trim();
                if (!registry.TryGet(name, out var descriptor))
                    return CommandResult.Fail($"指令不存在: {name}");

                var row = SnapshotCore(registry, exporter, governance, policy())
                    .First(item => item.CommandName.Equals(descriptor.Name, StringComparison.OrdinalIgnoreCase));
                var parameters = descriptor.Parameters.Select(parameter => new CommandParameterInfo(
                    parameter.Name,
                    parameter.Type.ToString().ToLowerInvariant(),
                    parameter.Required,
                    parameter.Default,
                    parameter.Position,
                    parameter.AllowedValues ?? [],
                    parameter.Description)).ToList();
                var tool = exporter.Find(descriptor.Name);
                var schema = tool?.InputSchema.ToJsonString(PrettyJson);
                var detail = new CommandCatalogDetail(row, parameters, schema)
                {
                    Annotations = descriptor.Annotations,
                };

                var text = new StringBuilder(
                    $"{descriptor.Name} "
                    + $"[{row.Domain}/{CommandClassLabels.Display(row.CommandClass)}/{row.McpState}]\n"
                    + descriptor.Summary);
                if (!string.IsNullOrWhiteSpace(descriptor.Example))
                    text.Append($"\n示例: {descriptor.Example}");
                if (row.HardExclusionReason != null)
                    text.Append($"\nMCP 硬排除: {row.HardExclusionReason}");
                return CommandResult.Ok(text.ToString(), detail);
            }),
        };

    private static CommandDescriptor BuildDomains(CommandRegistry registry) => new()
    {
        Name = "vulcan.command.domains",
        Domain = "vulcan",
        CommandClass = "command",
        Summary = "列出全部指令域及注册数量",
        Readonly = true,
        Example = "vulcan.command.domains",
        Handler = CommandDescriptor.Sync(_ =>
        {
            var rows = registry.All()
                .GroupBy(item => registry.GetDomain(item.Name), StringComparer.OrdinalIgnoreCase)
                .Select(group => new CommandDomainInfo(group.Key, group.Count()))
                .OrderBy(item => item.Domain, StringComparer.Ordinal)
                .ToList();
            return CommandResult.Ok(
                "指令域:" + string.Concat(rows.Select(item => $"\n  {item.Domain,-16} {item.Count}")), rows);
        }),
    };

    private static CommandDescriptor BuildManual(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        Func<string> policy) => new()
        {
            Name = "vulcan.command.manual",
            Domain = "vulcan",
            CommandClass = "command",
            Summary = "从运行时注册表和 MCP 投影预览或生成 Markdown 命令手册",
            Example = "vulcan.command.manual file=command-manual.md apply=false",
            Parameters =
        [
            new ParameterSpec
            {
                Name = "file",
                Description = "相对当前工作目录的 Markdown 输出路径",
                Required = true,
                Position = 0,
            },
            new ParameterSpec
            {
                Name = "apply",
                Description = "false 仅预览；true 经本地确认后原子写入",
                Type = ParamType.Bool,
                Default = "false",
            },
        ],
            Level = CommandLevel.Ask,
            ConfirmPrompt = context => context.GetBool("apply")
                ? $"确认生成命令手册 {context.GetString("file")}？只允许写入当前工作目录边界内的 .md 文件。"
                : null,
            Handler = CommandDescriptor.Sync(context =>
            {
                var relative = context.RequireString("file").Trim();
                if (Path.IsPathRooted(relative) || !relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("file 必须是当前工作目录内的相对 .md 路径");

                var root = Path.GetFullPath(Environment.CurrentDirectory);
                var target = Path.GetFullPath(Path.Combine(root, relative));
                var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                 + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    return CommandResult.Fail("命令手册路径越出当前工作目录");

                var markdown = CommandManualGenerator.Render(
                    registry, exporter, policy());
                var preview = new CommandManualPreview(
                    target, registry.All().Count, CommandManualGenerator.Sha256(markdown),
                    markdown, context.GetBool("apply"));
                if (!context.GetBool("apply"))
                    return CommandResult.Ok(
                        $"命令手册预览: {preview.CommandCount} 条，SHA-256 {preview.Sha256}，尚未写入\n{target}",
                        preview);

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temp = target + $".tmp-{Guid.NewGuid():N}";
                try
                {
                    File.WriteAllText(temp, markdown, new UTF8Encoding(false));
                    File.Move(temp, target, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                        File.Delete(temp);
                }
                return CommandResult.Ok(
                    $"命令手册已生成: {preview.CommandCount} 条，SHA-256 {preview.Sha256}\n{target}",
                    preview);
            }),
        };

    private static ParameterSpec StringParam(
        string name, string description, bool required = false, int? position = null) => new()
        {
            Name = name,
            Description = description,
            Required = required,
            Position = position,
        };
}
