namespace HistoryVulcan.Core.Commands;

/// <summary>可跨进程同步的命令参数元数据。</summary>
public sealed record CommandParameterCapability(
    string Name,
    string Description,
    ParamType Type,
    bool Required,
    string? Default,
    int? Position,
    string[]? AllowedValues)
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static CommandParameterCapability From(ParameterSpec parameter) => new(
        parameter.Name,
        parameter.Description,
        parameter.Type,
        parameter.Required,
        parameter.Default,
        parameter.Position,
        parameter.AllowedValues?.ToArray());

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ParameterSpec ToParameter() => new()
    {
        Name = Name,
        Description = Description,
        Type = Type,
        Required = Required,
        Default = Default,
        Position = Position,
        AllowedValues = AllowedValues?.ToArray(),
    };
}

/// <summary>
/// 前端向服务端权威目录发布的执行能力。只携带稳定元数据，不序列化 handler。
/// </summary>
public sealed record FrontendCommandCapability(
    string Name,
    string Summary,
    string? Example,
    IReadOnlyList<CommandParameterCapability> Parameters,
    bool Readonly,
    bool Dangerous,
    bool RequiresUiThread,
    bool AllowUnspecifiedParameters,
    bool AllowMcpExecution,
    string Source)
{
    /// <summary>命令所属宿主或模块域；附加属性保持旧位置构造函数兼容。</summary>
    public string? Domain { get; init; }

    /// <summary>命令在域内的功能类；附加属性保持旧位置构造函数兼容。</summary>
    public string? CommandClass { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public static FrontendCommandCapability From(
        CommandDescriptor descriptor,
        string source) => new(
        descriptor.Name,
        descriptor.Summary,
        descriptor.Example,
        descriptor.Parameters.Select(CommandParameterCapability.From).ToList(),
        descriptor.Readonly,
        descriptor.IsDangerous,
        descriptor.RequiresUiThread,
        descriptor.AllowUnspecifiedParameters,
        descriptor.AllowMcpExecution,
        source)
        {
            Domain = CommandRegistry.ResolveDomain(descriptor, source),
            CommandClass = CommandRegistry.ResolveCommandClass(descriptor, source),
        };

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandDescriptor CreateProxy() => new()
    {
        Name = Name,
        Domain = Domain,
        CommandClass = CommandClass,
        Summary = Summary,
        Example = Example,
        Parameters = Parameters.Select(parameter => parameter.ToParameter()).ToList(),
        Readonly = Readonly,
        Dangerous = Dangerous,
        ConfirmPrompt = Dangerous ? _ => $"确认执行前端命令 {Name}？" : null,
        RequiresUiThread = RequiresUiThread,
        ExecutionSite = CommandExecutionSite.Frontend,
        AllowUnspecifiedParameters = AllowUnspecifiedParameters,
        AllowMcpExecution = AllowMcpExecution,
        Handler = _ => Task.FromResult(CommandResult.Fail("前端不可用")),
    };
}

/// <summary>一个前端应用在单次连接中发布的完整命令能力目录。</summary>
public sealed record FrontendCapabilityCatalog(
    string FrontendName,
    IReadOnlyList<FrontendCommandCapability> Commands);
