using System.Security.Cryptography;
using System.Text;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;

using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Extensibility.Mcp;

/// <summary>Provides this HistoryVulcan public contract member.</summary>
public sealed record CommandManualPreview(
    string Path,
    int CommandCount,
    string Sha256,
    string Markdown,
    bool Applied);

/// <summary>从运行时注册表和 MCP 投影生成确定性命令手册。</summary>
public static class CommandManualGenerator
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string Render(
        CommandRegistry registry,
        CommandSchemaExporter exporter,
        string policy)
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
        builder.AppendLine("> 来源：运行时 `CommandRegistry` 与 MCP 投影自动生成；请勿手工维护指令条目。");
        builder.AppendLine($"> 当前 MCP 策略：`{policy}`");
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
                    var tool = exporter.Find(command.Name);
                    var mcpState = McpExposurePolicy.State(command);
                    var visible = McpExposurePolicy.IsVisible(command, policy);

                    builder.AppendLine();
                    builder.AppendLine($"#### `{command.Name}`");
                    builder.AppendLine();
                    builder.AppendLine(Escape(command.Summary));
                    builder.AppendLine();
                    builder.AppendLine($"- 域：`{Escape(domain.Key)}`");
                    builder.AppendLine($"- 类：`{Escape(commandClass.Key)}`");
                    builder.AppendLine($"- 来源：`{Escape(source)}`");
                    builder.AppendLine($"- 级别：{(command.Level == CommandLevel.Ask ? "询问（执行前必须问过人）" : "运行")}");
                    builder.AppendLine($"- UI 线程：{(command.RequiresUiThread ? "是" : "否")}");
                    builder.AppendLine($"- MCP：`{mcpState}`，当前策略{(visible ? "可见" : "隐藏")}" +
                                       (tool != null ? $"，工具名 `{tool.ToolName}`" : string.Empty));
                    if (McpExposurePolicy.HardExclusionReason(command) is { } reason)
                        builder.AppendLine($"- MCP 排除原因：{Escape(reason)}");

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

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static string Sha256(string markdown)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown)));

    private static string Cell(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : Escape(value);

    private static string Escape(string value)
        => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
