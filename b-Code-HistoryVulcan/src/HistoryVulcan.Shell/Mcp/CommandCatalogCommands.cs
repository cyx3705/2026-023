using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using HistoryVulcan.Services.Mcp;
using System.IO;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.Commands;

namespace HistoryVulcan.Shell.Mcp;

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

public sealed record CommandParameterInfo(
    string Name,
    string Type,
    bool Required,
    string? Default,
    int? Position,
    IReadOnlyList<string> AllowedValues,
    string Description);

public sealed record CommandCatalogDetail(
    CommandCatalogRow Command,
    IReadOnlyList<CommandParameterInfo> Parameters,
    string? McpInputSchema);

public sealed record CommandDomainInfo(string Domain, int Count);

/// <summary>V2.1.3 全指令结构化目录，注册表是唯一上游。</summary>
public static class CommandCatalogCommands
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static void RegisterCore(CommandRegistry registry, string source = "app")
    {
        var exporter = new CommandSchemaExporter(registry);
        RegisterCatalog(registry, exporter, prompts: null, static () => null, source);
    }

    public static void RegisterAll(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        PromptGovernanceStore prompts,
        Func<McpGateway?> gateway,
        string source = "app")
        => RegisterCatalog(registry, exporter, prompts, gateway, source);

    private static void RegisterCatalog(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        PromptGovernanceStore? prompts,
        Func<McpGateway?> gateway,
        string source)
    {
        registry.Register(BuildList(registry, exporter, prompts, gateway), source);
        registry.Register(BuildShow(registry, exporter, prompts, gateway), source);
        registry.Register(BuildDomains(registry), source);
        registry.Register(BuildManual(registry, exporter, gateway), source);
    }

    public static IReadOnlyList<CommandCatalogRow> Snapshot(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        PromptGovernanceStore prompts,
        string policy)
        => SnapshotCore(registry, exporter, prompts, policy);

    private static IReadOnlyList<CommandCatalogRow> SnapshotCore(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        PromptGovernanceStore? prompts,
        string policy)
    {
        var tools = exporter.ExportTools().ToDictionary(
            tool => tool.CommandName, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> descriptions = prompts?.AllEffectiveDescriptions()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> openProposals = prompts == null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : prompts.ListProposals(openOnly: true, limit: 500)
                .GroupBy(item => item.Command, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, int> incidents = prompts == null
            ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            : prompts.ListIncidents(limit: 500)
                .GroupBy(item => item.Command, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return registry.All().Select(descriptor =>
        {
            tools.TryGetValue(descriptor.Name, out var tool);
            var rawSource = registry.GetSource(descriptor.Name);
            var module = rawSource.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
            var sourceName = module ? "module" : rawSource;
            var sourceDetail = module ? rawSource["module:".Length..] : null;
            var customized = descriptions.ContainsKey(descriptor.Name);
            var revision = customized ? prompts?.GetCurrentRevision(descriptor.Name)?.Id : null;

            return new CommandCatalogRow(
                descriptor.Name,
                registry.GetDomain(descriptor.Name),
                descriptor.Summary,
                descriptor.Example,
                descriptor.Parameters.Count,
                sourceName,
                sourceDetail,
                descriptor.IsDangerous,
                descriptor.RequiresUiThread,
                tool?.ToolName,
                McpExposurePolicy.State(descriptor),
                McpExposurePolicy.IsVisible(descriptor, policy),
                customized,
                revision,
                openProposals.GetValueOrDefault(descriptor.Name),
                incidents.GetValueOrDefault(descriptor.Name),
                McpExposurePolicy.HardExclusionReason(descriptor.Name))
            {
                CommandClass = registry.GetCommandClass(descriptor.Name),
                Method = CommandRegistry.GetMethod(descriptor.Name),
            };
        }).ToList();
    }

    private static CommandDescriptor BuildList(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        PromptGovernanceStore? prompts,
        Func<McpGateway?> gateway) => new()
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
                    registry, exporter, prompts, gateway()?.Policy ?? "readonly");
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
        PromptGovernanceStore? prompts,
        Func<McpGateway?> gateway) => new()
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

                var row = SnapshotCore(registry, exporter, prompts, gateway()?.Policy ?? "readonly")
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
                var detail = new CommandCatalogDetail(row, parameters, schema);

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
        Func<McpGateway?> gateway) => new()
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
                    registry, exporter, gateway()?.Policy ?? "readonly");
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
