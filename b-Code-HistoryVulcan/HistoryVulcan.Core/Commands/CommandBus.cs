using System.Globalization;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令总线(§5.2 生命周期):
/// 解析 → 查注册表 → 参数校验 → 拦截(二次确认) → 执行(可 UI 线程编组) → 结果回显。
/// 任何指令抛出的异常都被捕获:程序不崩溃,错误进控制台与日志文件(P0)。
/// 指令回显与普通日志共用 IShellLog 管道、不同类别(L-03):
///   回显 = "cmd:来源",结果 = "cmd:result:域",进度 = "cmd:progress:域"。
/// </summary>
/// <remarks>
/// 5.2 起**一行指令只解析一次**。此前回显脱敏、分类、执行、结果脱敏、载荷风险判定
/// 各自 <c>CommandParser.Parse</c> 一遍——同一行文本解析五次，配五个
/// <c>catch (CommandSyntaxException)</c>，每个分支各自决定语法错误时怎么办。
/// 现在解析结果随 <see cref="Request"/> 走完全程：语法错误只判定一次，
/// 后续环节读的是同一份 <see cref="ParsedCommand"/>，不再有「这里当错、那里当对」的空间。
/// </remarks>
public sealed class CommandBus
{
    /// <summary>回显类别前缀;控制台按此前缀识别指令行。</summary>
    public const string EchoCategoryPrefix = "cmd:";

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string ResultCategory = "cmd:result";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string ProgressCategory = "cmd:progress";

    private const string RedactedMarker = "[REDACTED]";

    private readonly CommandRegistry _registry;
    private readonly IShellLog _log;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandBus(CommandRegistry registry, IShellLog log)
    {
        _registry = registry;
        _log = log;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public CommandRegistry Registry => _registry;

    /// <summary>二次确认通道;未注入时带确认位的指令一律拒绝执行(安全缺省)。</summary>
    public IConfirmationService? Confirmation { get; set; }

    /// <summary>需要按客户端来源选择确认通道时使用；设置后优先于 Confirmation。</summary>
    public Func<CommandContext, string, bool>? ConfirmationRouter { get; set; }

    /// <summary>UI 线程上下文;RequiresUiThread 的指令经此编组。</summary>
    public SynchronizationContext? UiContext { get; set; }

    /// <summary>
    /// 界面命令中继。界面模块装载时填入，拆除时置回 null。
    /// </summary>
    /// <remarks>
    /// **总线自己从不调用它。** 4.7.0 之前描述符上有个 <c>ExecutionSite</c> 字段，
    /// 取 <c>Frontend</c> 的指令由总线自动改道到这里；界面从独立进程变成宿主内模块
    /// （DEC-008）之后，全仓再没有任何一处把它设成 <c>Frontend</c>，那条改道成了死码，
    /// 已随字段一并删除。
    ///
    /// 留下这个挂钩，是因为 <c>vulcan.app.{show,hide,close,focusconsole}</c> 这几条
    /// **名字在宿主域、实现在界面模块**：注册表强制 <c>module:X</c> 来源的指令归属
    /// 模块自己的域（见 <c>CommandRegistry.ResolveDomain</c>），界面因此无法直接注册
    /// 一条 <c>vulcan.*</c>。宿主注册壳、界面填实现，是这条归属规则下唯一的形状。
    ///
    /// 调用方只有 <c>ServiceCommands</c> 那几条，且必须显式判 null——界面没装载时
    /// 它就是 null，那时的正确答复是「界面未装载」而不是空引用。
    /// </remarks>
    public Func<string, string, CancellationToken, Task<CommandResult>>? FrontendExecutor { get; set; }

    /// <summary>
    /// 客户端模式下的远程总线。<see cref="ShouldUseRemoteCommand"/> 返回 true 时整条命令
    /// 交给服务；服务经界面中继发回的 UI 命令可用来源标签绕过此路由并在本地执行。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? RemoteExecutor { get; set; }

    /// <summary>
    /// 按命令文本和来源决定是否走远端；未设置时配了 <see cref="RemoteExecutor"/> 即整体走远端。
    /// </summary>
    /// <remarks>
    /// 5.2 删掉了并列的 <c>ShouldUseRemote</c>（只看来源的兼容委托）：宿主、测试和
    /// 已部署的七个模块里没有任何一处给它赋过值，它只是让这条判定多了一层
    /// <c>?? ... ?? true</c> 的三段回退，读的人要先确认前两层都没接才敢下结论。
    /// </remarks>
    public Func<string, string, bool>? ShouldUseRemoteCommand { get; set; }

    /// <summary>每条指令执行完毕后触发(状态栏摘要,S-03);在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

    /// <summary>
    /// Validates a command text against the current registry without routing, logging, confirmation, or execution.
    /// UI surfaces use this to reject stale menu references at construction time.
    ///
    /// 配置了 <see cref="RemoteExecutor"/> 时，本地查不到的指令判为**无法在本进程判定**
    /// 而不是无效：权威注册表在服务进程，<see cref="ExecuteAsync"/> 也会把这类指令中继过去。
    /// 4.0.0 前两者口径不一致——`ExecuteAsync` 中继、`Validate` 报"未知指令"——
    /// 因此一条命令从前端搬到服务侧后，引用它的菜单会在构建期直接抛异常，
    /// 尽管点下去其实能正常执行。
    ///
    /// 嵌入模式（无远端）下仍然硬报错：那时本地注册表就是权威，笔误必须当场暴露。
    /// </summary>
    public string? Validate(string text)
    {
        var request = Request.Of(text);
        if (request.SyntaxError != null)
            return request.SyntaxError;
        if (!_registry.TryGet(request.Name, out var descriptor))
            return RemoteExecutor != null ? null : $"未知指令: {request.Name}";
        return BindArguments(descriptor, request.Parsed!, out _);
    }

    /// <summary>
    /// 安静调用通道:执行指令但不回显、不入历史、不触发 <see cref="Executed"/>。
    ///
    /// <see cref="ExecuteAsync"/> 是**操作者通道**——每次调用都会把指令与结果写进日志,
    /// 供人和 AI 追溯。宿主自身的高频内部调用(逐键补全、状态轮询一类)若走那条路,
    /// 会把控制台灌满,问题不在延迟而在语义:那些调用不是"操作"。
    ///
    /// 本方法让宿主与模块之间可以按**命令名**而不是按**类型**集成:调用方只依赖一个
    /// 字符串和本总线,不依赖被调方的 CLR 契约,因此被调方可以自由演进而不触动宿主公开面。
    /// 面向用户的动作仍应走 <see cref="ExecuteAsync"/>,不要用本方法绕过审计。
    /// </summary>
    /// <param name="text">指令文本,语法与 <see cref="ExecuteAsync"/> 一致。</param>
    /// <param name="source">来源标签;仅用于确认路由与远端判定,不会被回显。</param>
    /// <param name="cancellation">取消令牌。</param>
    public async Task<CommandResult> InvokeAsync(
        string text,
        string source,
        CancellationToken cancellation = default)
    {
        var request = Request.Of(text);
        try
        {
            return await ExecuteCoreAsync(request, source, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 与 ExecuteAsync 同样的兜底(N-05):总线自身缺陷不得击穿宿主。
            // 安静通道不回显,但内部错误仍需留痕,否则故障会静默消失。
            return InternalFault(ex, $"安静调用 {Taxonomy(request).Domain}");
        }
    }

    /// <summary>
    /// 执行一行指令文本。source 为来源标签(C-01):UI / 手动 / 脚本:文件名 / layout。
    /// 返回值在指令(含异步长任务)完成后才落定;方法自身不抛异常。
    /// </summary>
    public async Task<CommandResult> ExecuteAsync(
        string text,
        string source,
        CancellationToken cancellation = default)
    {
        var request = Request.Of(text);
        var taxonomy = Taxonomy(request);

        // 1. 回显
        var displayText = RedactArguments(request);
        _log.Log(ShellLogLevel.Info, EchoCategoryPrefix + source, displayText);

        CommandResult result;
        try
        {
            result = await ExecuteCoreAsync(request, source, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 最后一道兜底(N-05):总线自身缺陷也不允许击穿宿主
            result = InternalFault(ex, taxonomy.Domain);
        }

        result = RedactResult(request, result);

        // 2. 结果回显(错误红色高亮由控制台按级别渲染,C-02)
        _log.Log(
            result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
            $"{ResultCategory}:{taxonomy.Domain}:{taxonomy.CommandClass}",
            (result.Success ? "✓ " : "✗ ") + result.Message);

        Executed?.Invoke(displayText, source, result);
        return result;
    }

    /// <summary>
    /// 总线自身故障的统一出口：日志与返回值都只带异常**类型名**。
    /// </summary>
    /// <remarks>
    /// 不带 <c>ex.Message</c> 是安全约束：处理器的异常消息里可能夹着入参，而这条日志进文件、进控制台，
    /// 结果还会回到远端（<c>FreezeBlockerTests.CommandExceptionsDoNotExposeSensitiveValues</c> 守这条）。
    /// 5.2 之前这里把同一个类型名打两遍——<c>总线内部错误(X): X</c>——读起来像「类型 X、原因 X」。
    /// </remarks>
    private CommandResult InternalFault(Exception ex, string where)
    {
        var typeName = ex.GetType().Name;
        _log.Log(ShellLogLevel.Error, EchoCategoryPrefix + "internal", $"总线内部错误于 {where}: {typeName}");
        return CommandResult.Fail($"总线内部错误: {typeName}");
    }

    private async Task<CommandResult> ExecuteCoreAsync(
        Request request,
        string source,
        CancellationToken cancellation)
    {
        var remote = RemoteExecutor;
        if (remote != null && (ShouldUseRemoteCommand?.Invoke(request.Text, source) ?? true))
            return await remote(request.Text, source, cancellation).ConfigureAwait(false);

        if (request.SyntaxError != null)
            return CommandResult.Fail(request.SyntaxError);

        // 查注册表(未知指令给出候选,§5.2 P1)
        if (!_registry.TryGet(request.Name, out var descriptor))
        {
            var suggestions = _registry.Suggest(request.Name);
            var hint = suggestions.Count > 0
                ? $"\n你是不是想输入: {string.Join(" / ", suggestions)} ?"
                : "\n输入 help 查看全部指令。";
            return CommandResult.Fail($"未知指令: {request.Name}{hint}");
        }

        // 参数校验
        var bindError = BindArguments(descriptor, request.Parsed!, out var values);
        if (bindError != null)
            return CommandResult.Fail($"{bindError}\n{FormatUsage(descriptor)}");

        var taxonomy = Taxonomy(request);
        var progress = new Progress<string>(line =>
            _log.Log(
                ShellLogLevel.Info,
                $"{ProgressCategory}:{taxonomy.Domain}:{taxonomy.CommandClass}",
                line));
        var context = new CommandContext(descriptor, values, source, progress, cancellation);

        // 拦截:二次确认(§5.2;T-08/R-06 需要询问的操作在「手输指令路径」的统一闸口)
        //
        // 问不问由 Level 决定,提示语才由 ConfirmPrompt 提供。两者的分工要点在于
        // **null 的含义不同**:没有 ConfirmPrompt 是「没写文案」,由这里补一句缺省的;
        // 而 ConfirmPrompt 调用后返回 null 是「这次不用问」(按参数动态豁免)。
        // 若把两种 null 混同,janus.github.identity 在 apply=false 那次也会弹框。
        var prompt = descriptor.Level != CommandLevel.Ask
            ? null
            : descriptor.ConfirmPrompt == null
                ? $"确认执行 {descriptor.Name}？"
                : descriptor.ConfirmPrompt.Invoke(context);
        if (prompt != null)
        {
            if (ConfirmationRouter != null)
            {
                if (!ConfirmationRouter(context, prompt))
                    return CommandResult.Fail("已取消(未获确认)");
            }
            else if (Confirmation == null)
                return CommandResult.Fail("该指令需要二次确认,但当前环境没有确认通道,已拒绝执行");
            else if (!Confirmation.Confirm(prompt))
                return CommandResult.Fail("已取消(用户未确认)");
        }

        // 执行(必要时编组 UI 线程)
        try
        {
            if (descriptor.RequiresUiThread && UiContext != null
                && SynchronizationContext.Current != UiContext)
            {
                return await OnUiThreadAsync(() => descriptor.Handler(context)).ConfigureAwait(false);
            }

            return await descriptor.Handler(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 处理器异常同样只报类型名，理由见 InternalFault。
            var typeName = ex.GetType().Name;
            _log.Log(
                ShellLogLevel.Error,
                EchoCategoryPrefix + "internal",
                $"{descriptor.Name} 执行异常: {typeName}");
            return CommandResult.Fail($"{descriptor.Name} 执行异常: {typeName}");
        }
    }

    private Task<CommandResult> OnUiThreadAsync(Func<Task<CommandResult>> action)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiContext!.Post(
            async _ =>
            {
                try
                {
                    tcs.SetResult(await action().ConfigureAwait(true));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            },
            null);
        return tcs.Task;
    }

    private (string Domain, string CommandClass) Taxonomy(Request request)
    {
        var name = request.Name;
        if (_registry.TryGet(name, out _))
            return (_registry.GetDomain(name), _registry.GetCommandClass(name));
        var dot = name.IndexOf('.');
        return (dot > 0 ? name[..dot] : "core", "core");
    }

    // ---------------------------------------------------------------- 敏感值脱敏

    /// <summary>回显用的指令文本：把声明为敏感的参数值换成占位符。</summary>
    private string RedactArguments(Request request)
    {
        if (request.Parsed is not { } parsed)
            return MaskEverythingAfterName(request.Text);

        var carriesSecretValue = CarriesSecretSettingValue(parsed);
        var parts = new List<string> { parsed.Name };
        parts.AddRange(parsed.Positionals.Select((value, index) =>
            CommandParser.QuoteArg(IsSensitivePosition(parsed, index) ? RedactedMarker : value)));
        parts.AddRange(parsed.Named.Select(pair =>
            $"{pair.Key}={CommandParser.QuoteArg(
                IsSensitiveName(pair.Key)
                || (carriesSecretValue && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase))
                    ? RedactedMarker
                    : pair.Value)}"));
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 解析不了就把命令名之后整段遮掉。
    /// </summary>
    /// <remarks>
    /// 5.2 之前是两条带超时的正则：先按 <c>键=值</c> 逐个遮，再判断该命令「可能带密钥」才整体遮。
    /// 但**解析失败时参数边界本就不可信**，而那个判断还要先查注册表——查不到的坏命令只被逐个遮，
    /// 正则没盖住的部分照样进日志。解析失败的命令一定执行不了，保留参数没有用处，遮全才是唯一不需要判断的答案。
    /// </remarks>
    private static string MaskEverythingAfterName(string text)
    {
        var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
        return separator < 0
            ? text
            : string.Concat(text.AsSpan(0, separator), " ", RedactedMarker);
    }

    /// <summary>结果脱敏：把入参里的敏感值从消息里抹掉，必要时连结构化载荷一并丢弃。</summary>
    private CommandResult RedactResult(Request request, CommandResult result)
    {
        // 解析失败的命令没有可信的入参，也就答不出「哪个值要抹」；
        // 它的结果只会是一句语法错误，原样返回。
        if (request.Parsed is not { } parsed)
            return result;

        var secrets = SensitiveValues(parsed)
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToList();

        var message = Mask(result.Message, secrets);
        var data = result.Data is string text
            ? Mask(text, secrets)
            : result.Data != null && HasSensitiveResultRisk(parsed, secrets)
                ? null
                : result.Data;

        if (message.Equals(result.Message, StringComparison.Ordinal) && ReferenceEquals(data, result.Data))
            return result;

        return new CommandResult
        {
            Success = result.Success,
            Message = message,
            Data = data,
        };
    }

    private static string Mask(string text, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets)
            text = text.Replace(secret, RedactedMarker, StringComparison.Ordinal);
        return text;
    }

    /// <summary>
    /// 结构化载荷是否可能带出密钥。
    ///
    /// 除了入参里带了密钥的情形，还要防住 <c>vulcan.app.get key=&lt;敏感键&gt;</c>——
    /// 那条命令的入参不敏感，返回值才是密钥本身。
    /// </summary>
    private static bool HasSensitiveResultRisk(ParsedCommand parsed, IReadOnlyList<string> secrets)
    {
        if (secrets.Count > 0)
            return true;
        if (!parsed.Name.Equals("vulcan.app.get", StringComparison.OrdinalIgnoreCase))
            return false;
        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        return key != null && IsSensitiveName(key);
    }

    private IEnumerable<string> SensitiveValues(ParsedCommand parsed)
    {
        var carriesSecretValue = CarriesSecretSettingValue(parsed);
        foreach (var pair in parsed.Named)
        {
            if (IsSensitiveName(pair.Key)
                || (carriesSecretValue && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase)))
            {
                yield return pair.Value;
            }
        }

        for (var index = 0; index < parsed.Positionals.Count; index++)
        {
            if (IsSensitivePosition(parsed, index))
                yield return parsed.Positionals[index];
        }
    }

    /// <summary>这条命令的 <c>value</c> 参数是不是在写一个密钥（<c>vulcan.app.set key=…</c>）。</summary>
    private static bool CarriesSecretSettingValue(ParsedCommand parsed)
    {
        if (!parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase))
            return false;
        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        return key != null && IsSensitiveName(key);
    }

    /// <summary>
    /// 位置参数是否敏感。
    /// </summary>
    /// <remarks>
    /// 5.2 之前这里另有一份写死的命令名单（<c>vulcan.web.token</c> / <c>vulcan.mcp.token</c>）：
    /// 两条命令分别在 3.13.0 与 4.4.0 就没了，名单守着两个不会再出现的名字，
    /// 而**任何新的 <c>*.token</c> 命令它都盖不住**（冻结合同点名批过这类按名字写的规则）。
    /// 现在把敏感名判据用在**命令名**上：叫 <c>…token</c> 的命令，第一个位置参数就是那个密钥。
    /// </remarks>
    private bool IsSensitivePosition(ParsedCommand parsed, int position)
    {
        if (CarriesSecretSettingValue(parsed))
            return position == 1;
        if (IsSensitiveName(parsed.Name))
            return position == 0;
        return _registry.TryGet(parsed.Name, out var descriptor)
               && descriptor.Parameters.Any(parameter =>
                   parameter.Position == position && IsSensitiveName(parameter.Name));
    }

    /// <summary>参数名、配置键或命令名是否表示一个密钥。分隔符不参与判断。</summary>
    private static bool IsSensitiveName(string name)
    {
        var normalized = name
            .Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 参数绑定与校验

    private static string? BindArguments(
        CommandDescriptor descriptor,
        ParsedCommand parsed,
        out IReadOnlyDictionary<string, string> values)
    {
        if (descriptor.AllowUnspecifiedParameters)
        {
            values = parsed.Named.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            return null;
        }

        var bound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        values = bound;

        // 位置参数 → 声明了 Position 的参数(按序)
        var positionalSpecs = descriptor.Parameters
            .Where(p => p.Position.HasValue)
            .OrderBy(p => p.Position!.Value)
            .ToList();
        if (parsed.Positionals.Count > positionalSpecs.Count)
            return $"多余的位置参数: {string.Join(" ", parsed.Positionals.Skip(positionalSpecs.Count))}";
        for (var i = 0; i < parsed.Positionals.Count; i++)
            bound[positionalSpecs[i].Name] = parsed.Positionals[i];

        // 键=值 参数
        foreach (var (key, value) in parsed.Named)
        {
            var spec = descriptor.Parameters.FirstOrDefault(
                p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (spec == null)
            {
                var known = string.Join(" ", descriptor.Parameters.Select(p => p.Name + "="));
                return $"未知参数: {key}=" + (known.Length > 0 ? $"(可用: {known})" : "(该指令不接受参数)");
            }

            bound[spec.Name] = value;
        }

        // 必填与类型
        foreach (var spec in descriptor.Parameters)
        {
            if (!bound.TryGetValue(spec.Name, out var value))
            {
                if (spec.Required)
                    return $"缺少必填参数: {spec.Name}=";
                continue;
            }

            var typeError = spec.Type switch
            {
                ParamType.Int when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为整数,实际: {value}",
                ParamType.Double when !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                    => $"参数 {spec.Name} 应为数值,实际: {value}",
                ParamType.Bool when !IsBoolText(value)
                    => $"参数 {spec.Name} 应为 true/false,实际: {value}",
                _ => null,
            };
            if (typeError != null)
                return typeError;

            if (spec.AllowedValues is { Length: > 0 }
                && !spec.AllowedValues.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                return $"参数 {spec.Name} 取值应为 {string.Join("/", spec.AllowedValues)},实际: {value}";
            }
        }

        return null;
    }

    private static bool IsBoolText(string value)
        => value.Equals("true", StringComparison.OrdinalIgnoreCase)
           || value.Equals("false", StringComparison.OrdinalIgnoreCase)
           || value is "1" or "0"
           || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
           || value.Equals("no", StringComparison.OrdinalIgnoreCase)
           || value.Equals("on", StringComparison.OrdinalIgnoreCase)
           || value.Equals("off", StringComparison.OrdinalIgnoreCase);

    /// <summary>用法行,如 "用法: vulcan.command.help name= pos=left/right/top/bottom/tab [target=] [ratio=]"。</summary>
    public static string FormatUsage(CommandDescriptor d)
    {
        var parts = d.Parameters.Select(p =>
        {
            var core = p.AllowedValues is { Length: > 0 }
                ? $"{p.Name}={string.Join("/", p.AllowedValues)}"
                : $"{p.Name}=";
            return p.Required ? core : $"[{core}]";
        });
        return $"用法: {d.Name} {string.Join(" ", parts)}".TrimEnd();
    }

    /// <summary>
    /// 一行指令解析一次的结果，贯穿回显、脱敏、分类与执行。<see cref="Parsed"/> 为 null 即语法错误，
    /// 此时 <see cref="Name"/> 退化为首个 token——够给日志分类，不足以当作参数边界。
    /// </summary>
    private readonly record struct Request(string Text, ParsedCommand? Parsed, string? SyntaxError, string Name)
    {
        internal static Request Of(string text)
        {
            var trimmed = text.Trim();
            try
            {
                var parsed = CommandParser.Parse(trimmed);
                return new Request(trimmed, parsed, null, parsed.Name);
            }
            catch (CommandSyntaxException ex)
            {
                var separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
                var name = separator >= 0 ? trimmed[..separator] : trimmed;
                return new Request(trimmed, null, $"语法错误: {ex.Message}", name);
            }
        }
    }
}


/// <summary>
/// 二次确认通道(§5.2 拦截器链的首个内置拦截器;T-08 / R-06 等危险操作依赖)。
/// Shell 层以模态对话框实现;无 UI 场景(脚本/测试)可注入自动拒绝或自动通过的实现。
/// </summary>
public interface IConfirmationService
{
    /// <summary>返回 true 表示用户确认继续。</summary>
    bool Confirm(string prompt);
}
