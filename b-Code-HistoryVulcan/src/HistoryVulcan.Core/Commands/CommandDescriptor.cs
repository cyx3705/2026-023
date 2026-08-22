// 一条指令的形状：描述符本身，以及它的参数规格。
//
// ParameterSpec 只作为描述符的一部分存在，两者从来一起改，此前却是两个文件。

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 一条指令的注册模型(§5.3):名称、参数定义、执行体、帮助文本与二次确认。
///
/// 冻结契约:本类是地基类型,消费方能力**不得**再以新增字段的方式落地——用
/// <see cref="Annotations"/>。原 SupportsUndo 是「Q6 预留、首版不实现」的空位,
/// 接线至今零行为,已在冻结前删除:冻结会把预留位永久固化,而真要做撤销时,
/// 该重新设计而不是继承一个从未被验证过的字段。
/// </summary>
public sealed class CommandDescriptor
{
    /// <summary>完整指令名,小写,如 "vulcan.ui.dock"、"vulcan.command.help"。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 指令所属宿主或模块域。模块命令的有效域由模块宿主强制设置；
    /// 未声明的旧指令由注册表按旧命令前缀兼容推导。
    /// </summary>
    public string? Domain { get; init; }

    /// <summary>
    /// 域内功能类，如 app、log、win。未声明的模块命令归入 core，
    /// 未声明的旧非模块指令由注册表按旧命令前缀兼容推导。
    /// </summary>
    public string? CommandClass { get; init; }

    /// <summary>一句话说明(help 列表用)。</summary>
    public required string Summary { get; init; }

    /// <summary>示例行(help 详情用),如 "vulcan.ui.dock name=console pos=bottom ratio=0.25"。</summary>
    public string? Example { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = [];

    /// <summary>执行前需二次确认时,返回确认提示文本;null 表示无需确认(§5.2 拦截器)。</summary>
    public Func<CommandContext, string?>? ConfirmPrompt { get; init; }

    /// <summary>
    /// 代理描述符无法序列化原始确认函数时保留危险性元数据。
    /// 本地执行仍只由 <see cref="ConfirmPrompt"/> 触发确认；目录、MCP 与文档读取本合成属性。
    /// </summary>
    public bool Dangerous { get; init; }

    /// <summary>命令是否具有危险性元数据或本地确认闸口。</summary>
    public bool IsDangerous => Dangerous || ConfirmPrompt != null;

    /// <summary>
    /// 只读声明：命令不改变持久状态（不写库、文件或 Git 状态）。
    /// MCP 暴露策略优先读取此字段；外部名称白名单仅保留为兼容层。
    /// </summary>
    public bool Readonly { get; init; }

    /// <summary>true 时总线把执行体编组到 UI 线程(win.*/layout.* 等操作窗口的指令)。</summary>
    public bool RequiresUiThread { get; init; }

    /// <summary>
    /// 是否显式允许 MCP 执行。默认 false；仅查阅与治理不需要开启。
    /// 其余命令由既有只读、危险确认与策略规则决定是否暴露。
    /// </summary>
    public bool AllowMcpExecution { get; init; }

    /// <summary>
    /// 是否声明本指令可经命令行入口执行。默认 false。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="AllowMcpExecution"/> 的差别不只是面不同：MCP 那一面由
    /// <c>McpExposurePolicy</c> 按只读/危险/隐藏**推断**，本标记则是**逐条声明**，
    /// 缺省全关。CLI 是新面，没有存量指令要照顾，缺省全开等于把
    /// 「还没想过要不要暴露」写成「已经暴露」。判据见 <c>CliExposurePolicy</c>。
    /// </remarks>
    public bool AllowCliExecution { get; init; }

    /// <summary>代理描述符可接受任意参数并原样转发；本地业务命令不应开启。</summary>
    public bool AllowUnspecifiedParameters { get; init; }

    /// <summary>
    /// 消费方注解。总线自身**从不读取**本字典;它存在的唯一目的是让新的消费方能力
    /// 不再以「给本类加一个字段」的方式落地。
    ///
    /// 背景:本类历史上为每个消费方各长过一个字段——Domain / CommandClass(命令目录的
    /// 分类展示)、AllowMcpExecution(MCP 暴露)、ExecutionSite(前端路由,已随进程外前端一并删除)、
    /// SupportsUndo(至今未实现的预留位)。这些字段总线一个都不用,却让「命令描述符」这个地基类型
    /// 跟着每个消费方一起变,冻结因此无从谈起。
    ///
    /// 约定:键用 <c>&lt;消费方&gt;.&lt;能力&gt;</c>,如 <c>mcp.execute</c>、<c>catalog.hidden</c>。
    /// 消费方自行定义与解释自己的键,总线只负责原样携带。新增能力**不得**再加字段。
    /// </summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>读取一条消费方注解;不存在时返回 null。</summary>
    public string? Annotation(string key)
        => key != null && Annotations.TryGetValue(key, out var value) ? value : null;

    /// <summary>判定一条布尔注解是否为真;缺省与非法值均视为 false。</summary>
    public bool HasAnnotation(string key)
        => bool.TryParse(Annotation(key), out var value) && value;

    /// <summary>执行体。长任务应内部 await 后台工作并经 Progress 上报(§5.2 约束)。</summary>
    public required Func<CommandContext, Task<CommandResult>> Handler { get; init; }

    /// <summary>同步执行体的便捷包装。</summary>
    public static Func<CommandContext, Task<CommandResult>> Sync(Func<CommandContext, CommandResult> handler)
        => ctx => Task.FromResult(handler(ctx));
}

/// <summary>参数类型(校验用,§5.2 参数校验)。</summary>
public enum ParamType
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    String,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Int,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Double,
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Bool,
}

/// <summary>
/// 一个指令参数的定义(§5.3:名 / 类型 / 是否必填 / 默认值 / 说明)。
/// </summary>
public sealed class ParameterSpec
{
    /// <summary>参数名(键=值 的键),小写。</summary>
    public required string Name { get; init; }

    /// <summary>帮助文本里的一句话说明。</summary>
    public required string Description { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ParamType Type { get; init; } = ParamType.String;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Required { get; init; }

    /// <summary>缺省值的文本表达(帮助显示 + 取值兜底);null 表示无默认。</summary>
    public string? Default { get; init; }

    /// <summary>
    /// 允许按位置传入时的位置序号(0 起);null 表示只能 键=值。
    /// 例:help 的 command 参数 Position=0,支持 “help vulcan.ui.dock”。
    /// </summary>
    public int? Position { get; init; }

    /// <summary>枚举型取值约束(如 pos=left/right/top/bottom/tab);null 不限。</summary>
    public string[]? AllowedValues { get; init; }
}
