using System.Globalization;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令总线(§5.2 生命周期):
/// 解析 → 查注册表 → 参数校验 → 拦截(二次确认) → 执行(可 UI 线程编组) → 结果回显。
/// 任何指令抛出的异常都被捕获:程序不崩溃,错误进控制台与日志文件(P0)。
/// 指令回显与普通日志共用 IShellLog 管道、不同类别(L-03):
///   回显 = "cmd:来源",结果 = "cmd:result:域",进度 = "cmd:progress:域"。
/// </summary>
public sealed class CommandBus
{
    /// <summary>回显类别前缀;控制台按此前缀识别指令行。</summary>
    public const string EchoCategoryPrefix = "cmd:";

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string ResultCategory = "cmd:result";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string ProgressCategory = "cmd:progress";

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
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException ex)
        {
            return $"语法错误: {ex.Message}";
        }

        if (!_registry.TryGet(parsed.Name, out var descriptor))
            return RemoteExecutor != null ? null : $"未知指令: {parsed.Name}";

        return BindArguments(descriptor, parsed, out _);
    }

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
    /// 客户端模式下的远程总线。ShouldUseRemote 返回 true 时整条命令交给服务，
    /// 服务经界面中继发回的 UI 命令可用来源标签绕过此路由并在本地执行。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? RemoteExecutor { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public Func<string, bool>? ShouldUseRemote { get; set; }

    /// <summary>按命令文本和来源决定是否走远端；设置后优先于仅按来源的兼容委托。</summary>
    public Func<string, string, bool>? ShouldUseRemoteCommand { get; set; }

    /// <summary>
    /// 提示词治理的只读视图，由承载 MCP 的模块在装载时注入、拆除时清空。
    ///
    /// 放在总线上而不是新开一条注入通道：<see cref="Confirmation"/> 与
    /// <see cref="FrontendExecutor"/> 已经确立了「宿主留挂钩、模块填实现」这一模式，
    /// 而总线是模块经 <c>IModuleContext</c> 唯一拿得到的宿主共享对象。
    ///
    /// **消费方必须容忍 null。** 模块没装上、正在热重载、或装载失败时它就是 null，
    /// 此时目录指令照常可用，只是少了治理那几列——而不是整条指令消失。
    /// </summary>
    public IMcpPromptGovernanceView? McpGovernance { get; set; }

    /// <summary>每条指令执行完毕后触发(状态栏摘要,S-03);在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

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
        var trimmed = text.Trim();
        try
        {
            return await ExecuteCoreAsync(
                trimmed,
                source,
                TaxonomyOfCommandText(trimmed),
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 与 ExecuteAsync 同样的兜底(N-05):总线自身缺陷不得击穿宿主。
            // 安静通道不回显,但内部错误仍需留痕,否则故障会静默消失。
            var safeError = ex.GetType().Name;
            _log.Log(
                ShellLogLevel.Error,
                EchoCategoryPrefix + "internal",
                $"总线内部错误({safeError}) 于安静调用: {TaxonomyOfCommandText(trimmed).Domain}");
            return CommandResult.Fail($"总线内部错误: {safeError}");
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
        // 1. 回显
        var trimmed = text.Trim();
        var displayText = RedactSensitiveArguments(trimmed);
        var taxonomy = TaxonomyOfCommandText(trimmed);
        _log.Log(ShellLogLevel.Info, EchoCategoryPrefix + source, displayText);

        CommandResult result;
        try
        {
            result = await ExecuteCoreAsync(trimmed, source, taxonomy, cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = CommandResult.Fail("指令已取消");
        }
        catch (Exception ex)
        {
            // 最后一道兜底(N-05):总线自身缺陷也不允许击穿宿主
            var safeError = ex.GetType().Name;
            result = CommandResult.Fail($"总线内部错误: {safeError}");
            _log.Log(
                ShellLogLevel.Error,
                EchoCategoryPrefix + "internal",
                $"总线内部错误({ex.GetType().Name}): {safeError}");
        }

        result = RedactCommandResult(trimmed, result);

        // 2. 结果回显(错误红色高亮由控制台按级别渲染,C-02)
        _log.Log(
            result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
            $"{ResultCategory}:{taxonomy.Domain}:{taxonomy.CommandClass}",
            (result.Success ? "✓ " : "✗ ") + result.Message);

        Executed?.Invoke(displayText, source, result);
        return result;
    }

    private string RedactSensitiveArguments(string text)
    {
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException)
        {
            return RedactSensitiveFallback(text);
        }

        var redactValue = IsSecretSettingCommand(parsed);
        var parts = new List<string> { parsed.Name };
        parts.AddRange(parsed.Positionals.Select((value, index) =>
            CommandParser.QuoteArg(IsSensitivePosition(parsed, index)
                ? "[REDACTED]"
                : value)));
        parts.AddRange(parsed.Named.Select(pair =>
            $"{pair.Key}={CommandParser.QuoteArg(IsSensitiveArgument(pair.Key) ||
                                                  redactValue && pair.Key.Equals(
                                                      "value", StringComparison.OrdinalIgnoreCase)
                ? "[REDACTED]"
                : pair.Value)}"));
        return string.Join(' ', parts);
    }

    private string RedactSensitiveResult(string commandText, string message)
    {
        try
        {
            var parsed = CommandParser.Parse(commandText);
            foreach (var value in SensitiveValues(parsed)
                         .Where(value => !string.IsNullOrEmpty(value))
                         .Distinct(StringComparer.Ordinal)
                         .OrderByDescending(value => value.Length))
            {
                message = message.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            }
            return message;
        }
        catch (CommandSyntaxException)
        {
            return RedactSensitiveFallback(message);
        }
    }

    private IEnumerable<string> SensitiveValues(ParsedCommand parsed)
    {
        foreach (var pair in parsed.Named)
        {
            if (IsSensitiveArgument(pair.Key) ||
                IsSecretSettingCommand(parsed) && pair.Key.Equals("value", StringComparison.OrdinalIgnoreCase))
                yield return pair.Value;
        }

        for (var index = 0; index < parsed.Positionals.Count; index++)
        {
            if (IsSensitivePosition(parsed, index))
                yield return parsed.Positionals[index];
        }
    }

    private static bool IsSecretSettingCommand(ParsedCommand parsed)
    {
        if (parsed.Name.Equals("vulcan.web.token", StringComparison.OrdinalIgnoreCase) ||
            parsed.Name.Equals("vulcan.mcp.token", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase))
            return false;

        var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
        return key != null && IsSensitiveSettingKey(key);
    }

    private bool IsSensitivePosition(ParsedCommand parsed, int position)
    {
        if (parsed.Name.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase))
            return IsSecretSettingCommand(parsed) && position == 1;
        if (parsed.Name.Equals("vulcan.web.token", StringComparison.OrdinalIgnoreCase)
            || parsed.Name.Equals("vulcan.mcp.token", StringComparison.OrdinalIgnoreCase))
            return position == 0;
        return Registry.TryGet(parsed.Name, out var descriptor)
               && descriptor.Parameters.Any(parameter =>
                   parameter.Position == position && IsSensitiveArgument(parameter.Name));
    }

    private static bool IsSensitiveArgument(string name)
    {
        var normalized = NormalizeSensitiveName(name);
        return normalized.Equals("code", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("token", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("password", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("privatekey", StringComparison.OrdinalIgnoreCase)
               || normalized.EndsWith("connectionstring", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSensitiveSettingKey(string key)
        => IsSensitiveArgument(key);

    private static string NormalizeSensitiveName(string name)
        => name.Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);

    private string RedactSensitiveFallback(string text)
    {
        var redacted = Regex.Replace(
            text,
            "(?i)(\\b(?:code|(?:[a-z0-9_.-]*(?:token|password|passwd|secret|private[_-]?key|connection[_-]?string))|value)\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s]+)",
            "$1[REDACTED]",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

        var commandMatch = Regex.Match(
            text,
            "^\\s*(?<name>[A-Za-z_][\\w-]*(?:\\.[A-Za-z_][\\w-]*)*)(?=\\s|$)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        if (!commandMatch.Success)
            return redacted;

        var commandName = commandMatch.Groups["name"].Value;
        var mayContainSensitiveArguments = commandName.Equals("vulcan.app.set", StringComparison.OrdinalIgnoreCase)
                                           || IsSensitiveArgument(commandName)
                                           || Registry.TryGet(commandName, out var descriptor)
                                           && descriptor.Parameters.Any(parameter =>
                                               IsSensitiveArgument(parameter.Name));
        var argumentsStart = commandMatch.Index + commandMatch.Length;
        if (!mayContainSensitiveArguments ||
            string.IsNullOrWhiteSpace(text[argumentsStart..]))
            return redacted;

        // Parsing failed, so positional boundaries are no longer trustworthy. Mask the complete
        // remainder for commands that can carry secrets instead of risking a partial disclosure.
        return text[..argumentsStart] + " [REDACTED]";
    }

    private async Task<CommandResult> ExecuteCoreAsync(
        string text,
        string source,
        (string Domain, string CommandClass) taxonomy,
        CancellationToken cancellation)
    {
        var remote = RemoteExecutor;
        if (remote != null
            && (ShouldUseRemoteCommand?.Invoke(text, source)
                ?? ShouldUseRemote?.Invoke(source)
                ?? true))
            return await remote(text, source, cancellation).ConfigureAwait(false);

        // 解析
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException ex)
        {
            return CommandResult.Fail($"语法错误: {ex.Message}");
        }

        // 查注册表(未知指令给出候选,§5.2 P1)
        if (!_registry.TryGet(parsed.Name, out var descriptor))
        {
            var suggestions = _registry.Suggest(parsed.Name);
            var hint = suggestions.Count > 0
                ? $"\n你是不是想输入: {string.Join(" / ", suggestions)} ?"
                : "\n输入 help 查看全部指令。";
            return CommandResult.Fail($"未知指令: {parsed.Name}{hint}");
        }

        // 参数校验
        var bindError = BindArguments(descriptor, parsed, out var values);
        if (bindError != null)
            return CommandResult.Fail($"{bindError}\n{FormatUsage(descriptor)}");

        var progress = new Progress<string>(line =>
            _log.Log(
                ShellLogLevel.Info,
                $"{ProgressCategory}:{taxonomy.Domain}:{taxonomy.CommandClass}",
                line));
        var context = new CommandContext(descriptor, values, source, progress, cancellation);

        // 拦截:二次确认(§5.2;T-08/R-06 需要询问的操作在“手输指令路径”的统一闸口)
        //
        // 问不问由 Level 决定,提示语才由 ConfirmPrompt 提供。两者的分工要点在于
        // **null 的含义不同**:没有 ConfirmPrompt 是“没写文案”,由这里补一句缺省的;
        // 而 ConfirmPrompt 调用后返回 null 是“这次不用问”(按参数动态豁免)。
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
            var safeError = ex.GetType().Name;
            _log.Log(
                ShellLogLevel.Error,
                EchoCategoryPrefix + "internal",
                $"{descriptor.Name} 执行异常({ex.GetType().Name}): {safeError}");
            return CommandResult.Fail($"{descriptor.Name} 执行异常: {safeError}");
        }
    }

    private (string Domain, string CommandClass) TaxonomyOfCommandText(string text)
    {
        string name;
        try
        {
            name = CommandParser.Parse(text).Name;
        }
        catch (CommandSyntaxException)
        {
            var separator = text.IndexOfAny([' ', '\t', '\r', '\n']);
            name = separator >= 0 ? text[..separator] : text;
        }

        if (_registry.TryGet(name, out _))
            return (_registry.GetDomain(name), _registry.GetCommandClass(name));
        var dot = name.IndexOf('.');
        return (dot > 0 ? name[..dot] : "core", "core");
    }

    private CommandResult RedactCommandResult(string commandText, CommandResult result)
    {
        var message = RedactSensitiveResult(commandText, result.Message);
        var data = result.Data is string text
            ? RedactSensitiveResult(commandText, text)
            : result.Data != null && HasSensitiveResultRisk(commandText)
                ? null
                : result.Data;
        if (message.Equals(result.Message, StringComparison.Ordinal)
            && ReferenceEquals(data, result.Data))
            return result;
        return new CommandResult
        {
            Success = result.Success,
            Message = message,
            Data = data,
        };
    }

    private bool HasSensitiveResultRisk(string commandText)
    {
        try
        {
            var parsed = CommandParser.Parse(commandText);
            if (SensitiveValues(parsed).Any(value => !string.IsNullOrEmpty(value)))
                return true;
            if (!parsed.Name.Equals("vulcan.app.get", StringComparison.OrdinalIgnoreCase))
                return false;
            var key = parsed.Named.GetValueOrDefault("key") ?? parsed.Positionals.FirstOrDefault();
            return key != null && IsSensitiveSettingKey(key);
        }
        catch (CommandSyntaxException)
        {
            return false;
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

    /// <summary>用法行,如 "用法: vulcan.ui.dock name= pos=left/right/top/bottom/tab [target=] [ratio=]"。</summary>
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
