namespace HistoryVulcan.Core.Commands;

/// <summary>
/// Shared metadata for commands that execute locally in both the desktop shell and a service host.
/// Hosts bind only their execution handler and UI-thread capability.
/// </summary>
public static class BuiltinCommandDefinitions
{
    private static readonly IReadOnlyDictionary<string, Definition> Definitions = CreateDefinitions();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static IReadOnlyList<string> Names { get; } = Definitions.Keys.ToArray();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static bool Contains(string name) => Definitions.ContainsKey(name);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static CommandDescriptor Bind(
        string name,
        Func<CommandContext, Task<CommandResult>> handler,
        bool requiresUiThread = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        if (!Definitions.TryGetValue(name, out var definition))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown shared built-in command");

        return new CommandDescriptor
        {
            Name = definition.Name,
            Domain = "vulcan",
            CommandClass = definition.CommandClass,
            Summary = definition.Summary,
            Example = definition.Example,
            Parameters = definition.Parameters.Select(Clone).ToList(),
            ConfirmPrompt = definition.ConfirmPrompt,
            Dangerous = definition.Dangerous,
            Readonly = definition.Readonly,
            RequiresUiThread = requiresUiThread,
            Handler = handler,
        };
    }

    private static IReadOnlyDictionary<string, Definition> CreateDefinitions()
    {
        var definitions = new[]
        {
            new Definition(
                "vulcan.command.help",
                "command",
                "列出全部指令 / 显示某指令详情与示例",
                "vulcan.command.help vulcan.ui.dock",
                [Parameter("command", "指令名;省略时列出全部指令", position: 0)],
                Readonly: true),
            new Definition(
                "vulcan.app.get",
                "app",
                "读应用配置项;不带参数列出全部",
                "vulcan.app.get key=console.history",
                [Parameter("key", "配置键;省略列出全部", position: 0)],
                Readonly: true),
            new Definition(
                "vulcan.app.set",
                "app",
                "写应用配置项",
                "vulcan.app.set key=console.history value=1000",
                [
                    Parameter("key", "配置键", required: true, position: 0),
                    Parameter("value", "配置值", required: true, position: 1),
                ]),
            new Definition(
                "vulcan.app.opendata",
                "app",
                "在系统资源管理器中打开应用数据目录"),
        };

        return definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static ParameterSpec Parameter(
        string name,
        string description,
        ParamType type = ParamType.String,
        bool required = false,
        string? defaultValue = null,
        int? position = null)
        => new()
        {
            Name = name,
            Description = description,
            Type = type,
            Required = required,
            Default = defaultValue,
            Position = position,
        };

    private static ParameterSpec Clone(ParameterSpec source) => new()
    {
        Name = source.Name,
        Description = source.Description,
        Type = source.Type,
        Required = source.Required,
        Default = source.Default,
        Position = source.Position,
        AllowedValues = source.AllowedValues?.ToArray(),
    };

    private sealed record Definition(
        string Name,
        string CommandClass,
        string Summary,
        string? Example = null,
        IReadOnlyList<ParameterSpec>? ParameterList = null,
        bool Readonly = false,
        bool Dangerous = false,
        Func<CommandContext, string?>? ConfirmPrompt = null)
    {
        public IReadOnlyList<ParameterSpec> Parameters { get; } = ParameterList ?? [];
    }
}
