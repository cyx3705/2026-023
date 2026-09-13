using HistoryVulcan.Core.Logging;

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

    /// <summary>二次确认通道;未注入时带确认位的指令一律拒绝执行(安全缺省)。</summary>
    public IConfirmationService? Confirmation { get; set; }

    /// <summary>需要按客户端来源选择确认通道时使用；设置后优先于 Confirmation。</summary>
    public Func<CommandContext, string, bool>? ConfirmationRouter { get; set; }

    /// <summary>UI 线程上下文;RequiresUiThread 的指令经此编组。</summary>
    public SynchronizationContext? UiContext { get; set; }

    /// <summary>
    /// 界面命令中继。界面模块装载时填入，拆除时置回 null。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? FrontendExecutor { get; set; }

    /// <summary>
    /// 客户端模式下的远程总线。<see cref="ShouldUseRemoteCommand"/> 返回 true 时整条命令
    /// 交给服务；服务经界面中继发回的 UI 命令可用来源标签绕过此路由并在本地执行。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? RemoteExecutor { get; set; }

    /// <summary>
    /// 按命令文本和来源决定是否走远端；未设置时配了 <see cref="RemoteExecutor"/> 即整体走远端。
    /// </summary>
    public Func<string, string, bool>? ShouldUseRemoteCommand { get; set; }

    /// <summary>每条指令执行完毕后触发(状态栏摘要,S-03);在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

    /// <summary>
    /// 只验证语法和参数，不执行命令。配置远程执行器时，未知本地命令交由远端判定。
    /// </summary>
    public string? Validate(string text)
    {
        var request = CommandRequest.Create(text, _registry);
        if (request.SyntaxError != null)
            return request.SyntaxError;
        if (request.Descriptor is not { } descriptor)
            return RemoteExecutor != null ? null : $"未知指令: {request.Name}";
        return CommandArguments.Bind(descriptor, request.Parsed!, out _);
    }

    /// <summary>
    /// 内部调用：不回显、不触发 <see cref="Executed"/>，保持 Data 原值；进度日志仍脱敏。
    /// 面向用户的动作使用 <see cref="ExecuteAsync"/>。
    /// </summary>
    /// <param name="text">指令文本,语法与 <see cref="ExecuteAsync"/> 一致。</param>
    /// <param name="source">来源标签;仅用于确认路由与远端判定,不会被回显。</param>
    /// <param name="cancellation">取消令牌。</param>
    public async Task<CommandResult> InvokeAsync(
        string text,
        string source,
        CancellationToken cancellation = default)
    {
        var request = CommandRequest.Create(text, _registry);
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
            return InternalFault(ex, $"安静调用 {request.Domain}");
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
        var request = CommandRequest.Create(text, _registry);

        // 1. 回显
        var displayText = request.DisplayText();
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
            result = InternalFault(ex, request.Domain);
        }

        result = request.RedactResult(result);

        // 2. 结果回显(错误红色高亮由控制台按级别渲染,C-02)
        _log.Log(
            result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
            $"{ResultCategory}:{request.Domain}:{request.CommandClass}",
            (result.Success ? "✓ " : "✗ ") + result.Message);

        Executed?.Invoke(displayText, source, result);
        return result;
    }

    /// <summary>
    /// 总线自身故障的统一出口：日志与返回值都只带异常**类型名**。
    /// </summary>
    private CommandResult InternalFault(Exception ex, string where)
    {
        var typeName = ex.GetType().Name;
        _log.Log(ShellLogLevel.Error, EchoCategoryPrefix + "internal", $"总线内部错误于 {where}: {typeName}");
        return CommandResult.Fail($"总线内部错误: {typeName}");
    }

    private async Task<CommandResult> ExecuteCoreAsync(
        CommandRequest request,
        string source,
        CancellationToken cancellation)
    {
        var remote = RemoteExecutor;
        if (remote != null && (ShouldUseRemoteCommand?.Invoke(request.Text, source) ?? true))
            return await remote(request.Text, source, cancellation).ConfigureAwait(false);

        if (request.SyntaxError != null)
            return CommandResult.Fail(request.SyntaxError);

        // 查注册表(未知指令给出候选,§5.2 P1)
        if (request.Descriptor is not { } descriptor)
        {
            var suggestions = _registry.Suggest(request.Name);
            var hint = suggestions.Count > 0
                ? $"\n你是不是想输入: {string.Join(" / ", suggestions)} ?"
                : "\n输入 help 查看全部指令。";
            return CommandResult.Fail($"未知指令: {request.Name}{hint}");
        }

        // 参数校验
        var bindError = CommandArguments.Bind(descriptor, request.Parsed!, out var values);
        if (bindError != null)
            return CommandResult.Fail($"{bindError}\n{FormatUsage(descriptor)}");

        var progress = new Progress<string>(line =>
            _log.Log(
                ShellLogLevel.Info,
                $"{ProgressCategory}:{request.Domain}:{request.CommandClass}",
                request.Mask(line)));
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
