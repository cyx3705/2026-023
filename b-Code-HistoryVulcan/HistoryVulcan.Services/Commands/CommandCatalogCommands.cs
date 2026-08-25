using System.IO;
using System.Security.Cryptography;
using System.Text;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Commands;

/// <summary>目录中的一行：一条指令的注册事实与总线安全标志。</summary>
/// <param name="CommandName">完整指令名。</param>
/// <param name="Domain">所属指令域。</param>
/// <param name="Summary">一句话说明。</param>
/// <param name="Example">示例调用；无示例为 null。</param>
/// <param name="ParameterCount">参数个数。</param>
/// <param name="Source">注册方，如 framework:service 或 frontend:*。</param>
/// <param name="SourceDetail">注册方补充信息；无则为 null。</param>
/// <param name="Dangerous">级别是否为「询问」（执行前必须问过人）。</param>
/// <param name="RequiresUiThread">是否必须在 UI 线程执行。</param>
/// <param name="Readonly">是否声明为只读。</param>
/// <param name="HiddenReason">远端隐藏原因；未隐藏为 null。</param>
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
    bool Readonly,
    string? HiddenReason)
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

/// <summary>单条指令的完整详情：目录行与参数表。</summary>
/// <param name="Command">该指令的目录行。</param>
/// <param name="Parameters">参数表。</param>
public sealed record CommandCatalogDetail(
    CommandCatalogRow Command,
    IReadOnlyList<CommandParameterInfo> Parameters)
{
    /// <summary>命令注册方提供、由具体消费方解释的目录注解。</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一个指令域及其注册数量。</summary>
/// <param name="Domain">域名。</param>
/// <param name="Count">该域下的指令数。</param>
public sealed record CommandDomainInfo(string Domain, int Count);

/// <summary>命令手册预览：路径、条数、哈希与是否已写入。</summary>
internal sealed record CommandManualPreview(
    string Path,
    int CommandCount,
    string Sha256,
    string Markdown,
    bool Applied);

/// <summary>V2.1.3 全指令结构化目录，注册表是唯一上游。</summary>
public static class CommandCatalogCommands
{
    /// <summary>
    /// 注册核心目录指令。4.0.0 从 internal 提为 public：调用方在独立程序集，
    /// InternalsVisibleTo 对外部消费方不成立。
    /// </summary>
    public static void RegisterCore(CommandRegistry registry, string source = "app")
        => RegisterAll(registry, source);

    /// <summary>把目录查询指令注册进指定注册表。</summary>
    public static void RegisterAll(CommandRegistry registry, string source = "app")
    {
        registry.Register(BuildCliList(), source);
        registry.Register(BuildCliShow(), source);
        registry.Register(BuildList(registry), source);
        registry.Register(BuildShow(registry), source);
        registry.Register(BuildDomains(registry), source);
        registry.Register(BuildManual(registry), source);
    }

    private static CommandDescriptor BuildCliList() => new()
    {
        Name = "vulcan.cli.list",
        Domain = "vulcan",
        CommandClass = "cli",
        Summary = "列出 CLI 白名单、执行目标和副作用等级",
        Readonly = true,
        Example = "vulcan.cli.list",
        Handler = CommandDescriptor.Sync(_ =>
        {
            var rows = CliExposurePolicy.ExposedCommands.Select(name => new
            {
                Name = name,
                Mode = name.StartsWith("portunus.", StringComparison.OrdinalIgnoreCase)
                    ? "runtime-only"
                    : "offline",
                SideEffect = name.EndsWith("list", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("show", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("status", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("domains", StringComparison.OrdinalIgnoreCase)
                    ? "read"
                    : "write",
                ExitCodes = "0=success,1=execution-failure,2=usage/refused,3=runtime-unreachable",
            }).ToList();
            return CommandResult.Ok($"CLI 白名单: {rows.Count} 条\n"
                + string.Join("\n", rows.Select(row => $"  {row.Name} [{row.Mode}/{row.SideEffect}]")), rows);
        }),
    };

    private static CommandDescriptor BuildCliShow() => new()
    {
        Name = "vulcan.cli.show",
        Domain = "vulcan",
        CommandClass = "cli",
        Summary = "查看一条 CLI 指令的参数和执行边界",
        Readonly = true,
        Example = "vulcan.cli.show name=vulcan.dev.submit",
        Parameters = [StringParam("name", "CLI 指令名", required: true, position: 0)],
        Handler = CommandDescriptor.Sync(context =>
        {
            var name = context.RequireString("name").Trim();
            var exposed = CliExposurePolicy.ExposedCommands.FirstOrDefault(item =>
                item.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exposed == null)
                return CommandResult.Fail($"指令 {name} 不在 CLI 白名单中；请使用 vulcan.cli.list。\n"
                    + "运行中的宿主请改用 HistoryVulcan.Cli.exe --runtime。");
            var mode = exposed.StartsWith("portunus.", StringComparison.OrdinalIgnoreCase)
                ? "runtime-only" : "offline";
            return CommandResult.Ok(
                $"{exposed}\n执行目标: {mode}\n"
                + "副作用: 由命令描述符确认级别决定；runtime 动作必须显式 --approve。",
                new { Name = exposed, Mode = mode, Approval = mode == "runtime-only" ? "--approve for actions" : "none" });
        }),
    };

    /// <summary>按当前注册表生成目录快照。</summary>
    public static IReadOnlyList<CommandCatalogRow> Snapshot(CommandRegistry registry)
        => registry.All().Select(descriptor => ToRow(registry, descriptor)).ToList();

    /// <summary>从运行时注册表生成 Markdown 命令手册。</summary>
    public static string RenderManual(CommandRegistry registry)
    {
        var commands = registry.All().OrderBy(command => command.Name, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        builder.AppendLine($"# {Escape(AppIdentity.Current.Name)} 命令手册");
        builder.AppendLine();
        builder.AppendLine("> [!IMPORTANT]");
        builder.AppendLine("> 本文件由运行时指令注册表自动生成。禁止手工增删或改写下方指令条目；");
        builder.AppendLine("> 需要更新时，请在程序控制台执行 `vulcan.command.manual file=<相对 Markdown 路径> apply=true`。");
        builder.AppendLine();
        builder.AppendLine($"> 版本：{AppIdentity.Current.Version}");
        builder.AppendLine("> 来源：运行时 `CommandRegistry` 自动生成；请勿手工维护指令条目。");
        builder.AppendLine($"> 指令总数：{commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        builder.AppendLine();
        builder.AppendLine($"<!-- command-count: {commands.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} -->");

        foreach (var domain in commands.GroupBy(
                     command => registry.GetDomain(command.Name),
                     StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.AppendLine();
            builder.AppendLine($"## {domain.Key} ({domain.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            foreach (var commandClass in domain.GroupBy(
                         command => registry.GetCommandClass(command.Name),
                         StringComparer.OrdinalIgnoreCase)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                builder.AppendLine();
                builder.AppendLine($"### {commandClass.Key} ({commandClass.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)})");
                foreach (var command in commandClass)
                {
                    var source = registry.GetSource(command.Name);
                    builder.AppendLine();
                    builder.AppendLine($"#### `{command.Name}`");
                    builder.AppendLine();
                    builder.AppendLine(Escape(command.Summary));
                    builder.AppendLine();
                    builder.AppendLine($"- 域：`{Escape(domain.Key)}`");
                    builder.AppendLine($"- 类：`{Escape(commandClass.Key)}`");
                    builder.AppendLine($"- 来源：`{Escape(source)}`");
                    builder.AppendLine($"- 级别：{(command.Level == CommandLevel.Ask ? "询问（执行前必须问过人）" : "运行")}");
                    builder.AppendLine($"- 只读：{(command.Readonly ? "是" : "否")}");
                    builder.AppendLine($"- UI 线程：{(command.RequiresUiThread ? "是" : "否")}");
                    if (command.HiddenReason is { Length: > 0 } hidden)
                        builder.AppendLine($"- 远端隐藏：{Escape(hidden)}");

                    if (command.Parameters.Count > 0)
                    {
                        builder.AppendLine();
                        builder.AppendLine("| 参数 | 类型 | 必填 | 默认值 | 允许值 | 说明 |");
                        builder.AppendLine("|---|---|---|---|---|---|");
                        foreach (var parameter in command.Parameters)
                        {
                            builder.AppendLine(
                                $"| `{Escape(parameter.Name)}` | `{parameter.Type.ToString().ToLowerInvariant()}` | " +
                                $"{(parameter.Required ? "是" : "否")} | {Cell(parameter.Default)} | " +
                                $"{Cell(parameter.AllowedValues is { Length: > 0 } ? string.Join(" / ", parameter.AllowedValues) : null)} | " +
                                $"{Cell(parameter.Description)} |");
                        }
                    }
                    else
                    {
                        builder.AppendLine();
                        builder.AppendLine("参数：无。");
                    }

                    if (!string.IsNullOrWhiteSpace(command.Example))
                    {
                        builder.AppendLine();
                        builder.AppendLine("```text");
                        builder.AppendLine(command.Example);
                        builder.AppendLine("```");
                    }
                }
            }
        }

        return builder.ToString().Replace("\r\n", "\n");
    }

    /// <summary>手册正文的 SHA-256 十六进制摘要。</summary>
    public static string Sha256(string markdown)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));

    private static CommandCatalogRow ToRow(CommandRegistry registry, CommandDescriptor descriptor)
    {
        var rawSource = registry.GetSource(descriptor.Name);
        var module = rawSource.StartsWith("module:", StringComparison.OrdinalIgnoreCase);
        return new CommandCatalogRow(
            descriptor.Name,
            registry.GetDomain(descriptor.Name),
            descriptor.Summary,
            descriptor.Example,
            descriptor.Parameters.Count,
            module ? "module" : rawSource,
            module ? rawSource["module:".Length..] : null,
            descriptor.Level == CommandLevel.Ask,
            descriptor.RequiresUiThread,
            descriptor.Readonly,
            string.IsNullOrEmpty(descriptor.HiddenReason) ? null : descriptor.HiddenReason)
        {
            CommandClass = registry.GetCommandClass(descriptor.Name),
            Method = CommandRegistry.GetMethod(descriptor.Name),
        };
    }

    private static string Flag(CommandCatalogRow row)
    {
        if (row.HiddenReason is { Length: > 0 })
            return "hidden";
        if (row.Dangerous)
            return "ask";
        if (row.Readonly)
            return "readonly";
        return "run";
    }

    private static CommandDescriptor BuildList(CommandRegistry registry) => new()
    {
        Name = "vulcan.command.list",
        Domain = "vulcan",
        CommandClass = "command",
        Summary = "结构化列出全部注册指令及其来源与总线安全标志",
        Readonly = true,
        Example = "vulcan.command.list domain=vulcan class=win filter=dock",
        Parameters =
        [
            StringParam("domain", "可选指令域，如 proj / attr / command"),
            StringParam("class", "可选域内命令类，如 win / log / module"),
            StringParam("filter", "按名称、说明或来源搜索"),
        ],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            IEnumerable<CommandCatalogRow> rows = Snapshot(registry);
            var domain = ctx.GetString("domain")?.Trim();
            if (!string.IsNullOrWhiteSpace(domain))
                rows = rows.Where(row => row.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));

            var commandClass = ctx.GetString("class")?.Trim();
            if (!string.IsNullOrWhiteSpace(commandClass))
                rows = rows.Where(row => row.CommandClass.Equals(
                    commandClass,
                    StringComparison.OrdinalIgnoreCase));

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
                            + $"[{row.Domain}/{CommandClassLabels.Display(row.CommandClass)}/{Flag(row)}] "
                            + row.Summary);
            return CommandResult.Ok(text.ToString(), list);
        }),
    };

    private static CommandDescriptor BuildShow(CommandRegistry registry) => new()
    {
        Name = "vulcan.command.show",
        Domain = "vulcan",
        CommandClass = "command",
        Summary = "查看单条指令的 Help 参数、来源与总线安全标志",
        Readonly = true,
        Example = "vulcan.command.show name=vulcan.command.list",
        Parameters = [StringParam("name", "完整指令名", required: true, position: 0)],
        Handler = CommandDescriptor.Sync(ctx =>
        {
            var name = ctx.RequireString("name").Trim();
            if (!registry.TryGet(name, out var descriptor))
                return CommandResult.Fail($"指令不存在: {name}");

            var row = ToRow(registry, descriptor);
            var parameters = descriptor.Parameters.Select(parameter => new CommandParameterInfo(
                parameter.Name,
                parameter.Type.ToString().ToLowerInvariant(),
                parameter.Required,
                parameter.Default,
                parameter.Position,
                parameter.AllowedValues ?? [],
                parameter.Description)).ToList();
            var detail = new CommandCatalogDetail(row, parameters)
            {
                Annotations = descriptor.Annotations,
            };

            var text = new StringBuilder(
                $"{descriptor.Name} "
                + $"[{row.Domain}/{CommandClassLabels.Display(row.CommandClass)}/{Flag(row)}]\n"
                + descriptor.Summary);
            if (!string.IsNullOrWhiteSpace(descriptor.Example))
                text.Append($"\n示例: {descriptor.Example}");
            if (row.HiddenReason != null)
                text.Append($"\n远端隐藏: {row.HiddenReason}");
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

    private static CommandDescriptor BuildManual(CommandRegistry registry) => new()
    {
        Name = "vulcan.command.manual",
        Domain = "vulcan",
        CommandClass = "command",
        Summary = "从运行时注册表预览或生成 Markdown 命令手册",
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

            var markdown = RenderManual(registry);
            var preview = new CommandManualPreview(
                target, registry.All().Count, Sha256(markdown),
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

    private static string Cell(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : Escape(value);

    private static string Escape(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
