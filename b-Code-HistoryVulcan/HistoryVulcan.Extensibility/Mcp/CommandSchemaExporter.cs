using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Commands;

using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Extensibility.Mcp;

/// <summary>
/// 一条指令的 MCP 工具形态(MC-01):tools/list 条目与本地 vulcan.mcp.schema 共用。
/// Dangerous = 级别为「询问」(总线确认闸口),网关侧按 MS-04 对 MCP 一律拒绝执行。
/// </summary>
public sealed record McpToolInfo(
    string ToolName,
    string CommandName,
    string Description,
    JsonObject InputSchema,
    bool Dangerous,
    string DefaultDescription = "",
    bool Customized = false);

/// <summary>
/// 指令元数据自描述层(V2.1 §3,MC-01~06):
/// CommandRegistry 的指令元数据 ↔ MCP 工具描述(JSON Schema)双向映射。
/// 铁律 1:本层与网关的唯一上游是指令注册表/总线,不认识 ModuleHost 与任何 DLL 类型。
/// 每次调用现算不缓存(MC-04),模块热重载后的指令增删即刻反映。
/// </summary>
public sealed partial class CommandSchemaExporter
{
    private readonly CommandRegistry _registry;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandSchemaExporter(CommandRegistry registry) => _registry = registry;

    /// <summary>
    /// 生效提示词提供者(V2.1.2,装配点接 PromptGovernanceStore.AllEffectiveDescriptions):
    /// 修订在导出读取侧合成，应用后客户端下次 tools/list 即见新描述，无需重启。
    /// </summary>
    public Func<IReadOnlyDictionary<string, string>>? DescriptionsProvider { get; set; }

    /// <summary>全量导出(MC-01):注册表指令 − 硬排除,含危险标记与提示词覆盖;调用即现算。</summary>
    public IReadOnlyList<McpToolInfo> ExportTools()
    {
        var tools = new List<McpToolInfo>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var overrides = DescriptionsProvider?.Invoke();

        foreach (var descriptor in _registry.All())
        {
            if (McpExposurePolicy.HardExclusionReason(descriptor) != null)
                continue;

            var toolName = MakeToolName(descriptor.Name, usedNames);
            usedNames.Add(toolName);

            var defaultDescription = descriptor.Summary;
            if (!string.IsNullOrWhiteSpace(descriptor.Example))
                defaultDescription += $"\n示例: {descriptor.Example}";

            var custom = overrides?.GetValueOrDefault(descriptor.Name);
            var customized = !string.IsNullOrWhiteSpace(custom);

            tools.Add(new McpToolInfo(
                toolName,
                descriptor.Name,
                customized ? custom! : defaultDescription,
                BuildInputSchema(descriptor),
                Dangerous: descriptor.Level == CommandLevel.Ask,
                DefaultDescription: defaultDescription,
                Customized: customized));
        }

        return tools;
    }

    /// <summary>按工具名或指令名找一条(vulcan.mcp.schema name= 与 tools/call 共用)。</summary>
    public McpToolInfo? Find(string nameOrTool)
        => ExportTools().FirstOrDefault(t =>
            t.ToolName.Equals(nameOrTool, StringComparison.OrdinalIgnoreCase)
            || t.CommandName.Equals(nameOrTool, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- MC-03 工具名合法化

    /// <summary>指令名 → MCP 合法工具名(^[a-zA-Z0-9_-]{1,64}$):点等非法字符换下划线,冲突追加序号。</summary>
    public static string MakeToolName(string commandName, HashSet<string> used)
    {
        var name = IllegalChars().Replace(commandName, "_");
        if (name.Length > 60)
            name = name[^60..].TrimStart('_');
        if (name.Length == 0)
            name = "tool";

        var candidate = name;
        for (var i = 2; used.Contains(candidate); i++)
            candidate = $"{name}_{i}";
        return candidate;
    }

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex IllegalChars();

    // ---------------------------------------------------------------- MC-02 参数 → JSON Schema

    private static JsonObject BuildInputSchema(CommandDescriptor descriptor)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in descriptor.Parameters)
        {
            var prop = new JsonObject
            {
                ["type"] = p.Type switch
                {
                    ParamType.Int => "integer",
                    ParamType.Double => "number",
                    ParamType.Bool => "boolean",
                    _ => "string",
                },
            };

            if (!string.IsNullOrWhiteSpace(p.Description))
                prop["description"] = p.Description;

            if (p.Default != null)
                prop["default"] = DefaultNode(p.Type, p.Default);

            if (p.AllowedValues is { Length: > 0 })
            {
                var allowed = new JsonArray();
                foreach (var v in p.AllowedValues)
                    allowed.Add(JsonValue.Create(v));
                prop["enum"] = allowed;
            }

            properties[p.Name] = prop;
            if (p.Required)
                required.Add(JsonValue.Create(p.Name));
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    /// <summary>Default 是字符串表达,按声明类型转成 JSON 原生类型(转不动回落字符串)。</summary>
    private static JsonNode DefaultNode(ParamType type, string text) => type switch
    {
        ParamType.Int when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            => JsonValue.Create(i),
        ParamType.Double when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            => JsonValue.Create(d),
        ParamType.Bool => JsonValue.Create(
            text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1"),
        _ => JsonValue.Create(text),
    };

    // ---------------------------------------------------------------- MC-06 反向解析

    /// <summary>
    /// tools/call 的 arguments(JSON 对象) → 总线指令文本。
    /// 值编码复用 CommandParser.QuoteArg(§5.1 官方转义),与手输语法完全同轨;
    /// 嵌套对象/数组序列化为紧凑 JSON 字符串传入(由指令自己解释)。
    /// </summary>
    public static string BuildCommandText(string commandName, JsonElement? arguments)
    {
        if (arguments is not { ValueKind: JsonValueKind.Object } args)
            return commandName;

        var parts = new List<string> { commandName };
        foreach (var prop in args.EnumerateObject())
        {
            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => CommandParser.QuoteArg(prop.Value.GetString() ?? ""),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => prop.Value.GetRawText(),
                _ => CommandParser.QuoteArg(prop.Value.GetRawText()),
            };
            if (value != null)
                parts.Add($"{prop.Name}={value}");
        }

        return string.Join(' ', parts);
    }
}
