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
    /// <summary>完整指令名,小写,如 "vulcan.command.help"、"vulcan.command.help"。</summary>
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

    /// <summary>示例行(help 详情用),如 "vulcan.command.help name=console pos=bottom ratio=0.25"。</summary>
    public string? Example { get; init; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IReadOnlyList<ParameterSpec> Parameters { get; init; } = [];

    /// <summary>
    /// 本指令是直接跑，还是执行前必须问过人。
    /// </summary>
    /// <remarks>
    /// 这一件事此前由三个成员共同表达：<c>Dangerous</c>（元数据）、<c>ConfirmPrompt</c>
    /// （闸口）、以及合成的 <c>IsDangerous = Dangerous || ConfirmPrompt != null</c>。
    /// 三份表达意味着消费方各读各的：MCP 读合成属性、总线读闸口、目录读元数据，
    /// 于是「这条到底危不危险」在不同的面上可以给出不同答案。
    ///
    /// 现在只有本字段是权威。提示语仍在 <see cref="ConfirmPrompt"/> 里，
    /// 但它不再**决定**问不问——决定权在这里。两者不一致时注册表直接拒绝注册，
    /// 见 <c>CommandRegistry.Register</c>：只写 ConfirmPrompt 而忘了升级别，
    /// 后果是闸口静默失效，那是最不该靠人记住的一类错误。
    /// </remarks>
    public CommandLevel Level { get; init; }

    /// <summary>
    /// <see cref="CommandLevel.Ask"/> 时的确认提示文本。
    /// </summary>
    /// <remarks>
    /// 留空则由总线按指令名生成一句缺省提示。
    ///
    /// **返回 null 表示本次不问**——这是刻意保留的动态豁免，生产里在用：
    /// <c>janus.github.identity</c> 只在 <c>apply=true</c> 时才是写操作，
    /// <c>ConfirmPrompt = ctx =&gt; ctx.GetBool("apply") ? "…" : null</c>
    /// 让它在只读那次不弹无意义的确认框，而级别仍然是「询问」，
    /// 因此它对 MCP 的可见性不会因为参数不同而摇摆。
    /// </remarks>
    public Func<CommandContext, string?>? ConfirmPrompt { get; init; }

    /// <summary>
    /// 只读声明：命令不改变持久状态（不写库、文件或 Git 状态）。
    /// </summary>
    public bool Readonly { get; init; }

    /// <summary>
    /// 非 null 表示本指令**不对任何远端消费面暴露**，值是不暴露的原因。
    /// </summary>
    /// <remarks>
    /// 缺省 null，即暴露——因为这份指令集是长年累月长起来的，缺省全关等于一夜失能。
    ///
    /// 做成「写原因即隐藏」而不是一个布尔位，是因为**隐藏一条指令永远有具体理由**，
    /// 而理由是唯一能让后来人判断该不该继续隐藏的东西。目录页与手册直接显示它。
    ///
    /// 远端是否暴露只看本字段，不再另做一层按名字写的宿主排除名单。
    /// 那份名单失效过四次，每次都是同一个原因：**指令改了名，规则还盯着旧名字**——
    /// <c>debug.logflood</c> 收编为 <c>vulcan.log.flood</c>（承压注水指令因此可被远程触发）、
    /// <c>vulcan.mcp.*</c> 随网关迁出改名 <c>portunus.mcp.*</c>（把「关掉正在服务你的通道」
    /// 交给了远端）。声明挂在描述符上，改名时它跟着一起走，这类失效不再可能发生。
    ///
    /// 代价要写明：**新指令忘了写就是暴露的**。这是权衡后的选择——
    /// 缺省全关会让 153 条现有指令一夜失能，而那 153 条恰恰包含全部恢复路径。
    /// </remarks>
    public string? HiddenReason { get; init; }

    /// <summary>true 时总线把执行体编组到 UI 线程(win.*/layout.* 等操作窗口的指令)。</summary>
    public bool RequiresUiThread { get; init; }

    /// <summary>代理描述符可接受任意参数并原样转发；本地业务命令不应开启。</summary>
    public bool AllowUnspecifiedParameters { get; init; }

    /// <summary>
    /// 消费方注解。总线自身**从不读取**本字典;它存在的唯一目的是让新的消费方能力
    /// 不再以「给本类加一个字段」的方式落地。
    ///
    /// 背景:本类历史上为每个消费方各长过一个字段——Domain / CommandClass(命令目录的
    /// 分类展示)、AllowMcpExecution(MCP 暴露)、AllowCliExecution(命令行暴露)、
    /// ExecutionSite(前端路由)、SupportsUndo(从未实现的预留位)。后四个已经删除:
    /// 前三个是「每个消费面各发一个令牌」,现在暴露只有 HiddenReason 一处声明;
    /// 最后一个从未接线。这些字段总线一个都不用,却让「命令描述符」这个地基类型
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
    /// 例:help 的 command 参数 Position=0,支持 “help vulcan.command.help”。
    /// </summary>
    public int? Position { get; init; }

    /// <summary>枚举型取值约束(如 pos=left/right/top/bottom/tab);null 不限。</summary>
    public string[]? AllowedValues { get; init; }
}

/// <summary>指令的执行级别：跑，还是先问。</summary>
/// <remarks>
/// 只有两级，因为本程序完全自用，中间态没有服务对象。
/// 级别只回答「要不要问人」这一件事；改不改东西看 <c>Readonly</c>，
/// 谁能调看暴露声明，在哪个线程跑看 <c>RequiresUiThread</c>。
/// </remarks>
public enum CommandLevel
{
    /// <summary>运行：直接执行，不打断。</summary>
    Run,

    /// <summary>询问：执行前必须问过人；没有确认通道时拒绝执行，而不是放行。</summary>
    Ask,
}
