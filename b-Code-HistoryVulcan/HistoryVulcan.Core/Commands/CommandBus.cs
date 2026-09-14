using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 指令总线(§5.2 生命周期):
/// 解析 → 查注册表 → 参数校验 → 拦截(二次确认) → 执行(可 UI 线程编组) → 结果回显。
/// 任何指令抛出的异常都被捕获:程序不崩溃,错误进控制台与日志文件(P0)。
/// 指令回显与普通日志共用 IShellLog 管道、不同类别(L-03):
///   回显 = "cmd:来源",结果 = "cmd:result:域",进度 = "cmd:progress:域"。
/// </summary>
/// <remarks>
/// 5.4 起区分两种总线：模块自建的总线可以自由装配确认、界面线程与远端路由；
/// 宿主交给模块的那一条在装配完成后封口，这些开关只读，前端经
/// <see cref="IModuleContext.RegisterFrontend"/> 登记，同一宿主只允许一个。
/// </remarks>
public sealed class CommandBus
{
    /// <summary>回显类别前缀;控制台按此前缀识别指令行。</summary>
    public const string EchoCategoryPrefix = "cmd:";

    /// <summary>结果回显的日志类别前缀；完整类别为 <c>cmd:result:&lt;域&gt;:&lt;类&gt;</c>。</summary>
    public const string ResultCategory = "cmd:result";

    /// <summary>进度行的日志类别前缀；完整类别为 <c>cmd:progress:&lt;域&gt;:&lt;类&gt;</c>。</summary>
    public const string ProgressCategory = "cmd:progress";

    private readonly CommandRegistry _registry;
    private readonly IShellLog _log;
    private readonly object _frontendGate = new();
    private IConfirmationService? _confirmation;
    private SynchronizationContext? _uiContext;
    private Func<string, string, CancellationToken, Task<CommandResult>>? _remoteExecutor;
    private Func<string, string, bool>? _shouldUseRemoteCommand;
    private FrontendRegistration? _frontend;
    private volatile bool _hostSealed;

    /// <summary>创建一条绑定到指定注册表的总线；解析、校验与执行都以该注册表为准。</summary>
    /// <param name="registry">命令注册表。</param>
    /// <param name="log">回显、结果、进度与内部故障写入的日志管道。</param>
    public CommandBus(CommandRegistry registry, IShellLog log)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(log);
        _registry = registry;
        _log = log;
    }

    /// <summary>本总线解析与执行命令所用的注册表。</summary>
    public CommandRegistry Registry => _registry;

    /// <summary>
    /// 二次确认通道；没有登记前端且未设置时，带确认位的指令一律拒绝(安全缺省)。
    /// 宿主总线上只读，写入抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public IConfirmationService? Confirmation
    {
        get => _confirmation;
        set
        {
            EnsureConfigurable(nameof(Confirmation));
            _confirmation = value;
        }
    }

    /// <summary>
    /// UI 线程上下文;RequiresUiThread 的指令经此编组。登记了前端时返回前端的界面上下文。
    /// 宿主总线上只读，写入抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public SynchronizationContext? UiContext
    {
        get => Frontend?.UiContext ?? _uiContext;
        set
        {
            EnsureConfigurable(nameof(UiContext));
            _uiContext = value;
        }
    }

    /// <summary>
    /// 客户端模式下的远程总线。<see cref="ShouldUseRemoteCommand"/> 返回 true 时整条命令
    /// 交给远端，本地不做语法、确认与编组。宿主总线上只读。
    /// </summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? RemoteExecutor
    {
        get => _remoteExecutor;
        set
        {
            EnsureConfigurable(nameof(RemoteExecutor));
            _remoteExecutor = value;
        }
    }

    /// <summary>
    /// 按命令文本和来源决定是否走远端；未设置时配了 <see cref="RemoteExecutor"/> 即整体走远端。
    /// 宿主总线上只读。
    /// </summary>
    public Func<string, string, bool>? ShouldUseRemoteCommand
    {
        get => _shouldUseRemoteCommand;
        set
        {
            EnsureConfigurable(nameof(ShouldUseRemoteCommand));
            _shouldUseRemoteCommand = value;
        }
    }

    /// <summary>每条指令执行完毕后触发(状态栏摘要,S-03);在执行线程上引发。</summary>
    public event Action<string, string, CommandResult>? Executed;

    /// <summary>当前登记的前端；没有时为 null。</summary>
    internal IFrontend? Frontend
    {
        get
        {
            lock (_frontendGate)
                return _frontend?.Frontend;
        }
    }

    /// <summary>
    /// 在指令之外向当前确认通道发问：登记了前端时由前端回答，否则由 <see cref="Confirmation"/> 回答。
    /// </summary>
    /// <remarks>
    /// 用于处理器执行中途才知道要不要问的场合（例如按远端状态决定是否覆盖）。
    /// 能在执行前决定的确认应声明 <see cref="CommandLevel.Ask"/>，交给总线闸口。
    /// </remarks>
    /// <param name="prompt">面向用户的确认文案；调用方负责不含敏感值。</param>
    /// <returns>获得确认时为 true；没有任何确认通道时为 false。</returns>
    public bool RequestConfirmation(string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        return Confirm(prompt);
    }

    /// <summary>
    /// 只验证语法和参数，不执行命令。配置远程执行器时，未知本地命令交由远端判定。
    /// </summary>
    /// <param name="text">指令文本。</param>
    /// <returns>通过时为 null，否则为面向用户的错误说明。</returns>
    public string? Validate(string text)
    {
        var request = CommandRequest.Create(text, _registry);
        if (request.SyntaxError != null)
            return request.SyntaxError;
        if (request.Descriptor is not { } descriptor)
            return _remoteExecutor != null ? null : $"未知指令: {request.Name}";
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
    /// <param name="text">指令文本。</param>
    /// <param name="source">来源标签，进入回显类别与确认、远端判定。</param>
    /// <param name="cancellation">取消令牌。</param>
    public async Task<CommandResult> ExecuteAsync(
        string text,
        string source,
        CancellationToken cancellation = default)
    {
        var request = CommandRequest.Create(text, _registry);

        // 交给远端的命令由远端总线回显、记进度和结果（5.5.0，REQ-HOST-004）。本地再记一遍，
        // 界面与宿主共用同一份控制台日志时，每条命令就出现两次；而且本地注册表不认识远端命令，
        // 回显无法按参数脱敏。
        var remote = RoutesToRemote(request, source);

        // 1. 回显
        var displayText = request.DisplayText();
        if (!remote)
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
        if (!remote)
        {
            _log.Log(
                result.Success ? ShellLogLevel.Info : ShellLogLevel.Error,
                $"{ResultCategory}:{request.Domain}:{request.CommandClass}",
                (result.Success ? "✓ " : "✗ ") + result.Message);
        }

        Executed?.Invoke(displayText, source, result);
        return result;
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

    /// <summary>
    /// 登记前端。同一 owner 重复登记替换旧登记；其他 owner 已登记时拒绝。
    /// </summary>
    internal IDisposable ClaimFrontend(string owner, IFrontend frontend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(frontend);
        ArgumentNullException.ThrowIfNull(frontend.UiContext);

        var registration = new FrontendRegistration(this, owner.Trim(), frontend);
        lock (_frontendGate)
        {
            if (_frontend is { } current
                && !current.Owner.Equals(registration.Owner, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"前端已由 {current.Owner} 登记；同一宿主只允许一个前端。");
            }

            _frontend = registration;
        }

        return registration;
    }

    /// <summary>撤销某个 owner 的前端登记；模块卸载时由宿主调用，owner 不匹配时不做任何事。</summary>
    internal void ReleaseFrontend(string owner)
    {
        lock (_frontendGate)
        {
            if (_frontend is { } current
                && current.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
            {
                _frontend = null;
            }
        }
    }

    /// <summary>宿主装配完成后封口：此后公开开关只读。</summary>
    internal void SealHostWiring() => _hostSealed = true;

    /// <summary>宿主自身装配缺省确认通道；不受封口限制。</summary>
    internal void SetHostConfirmation(IConfirmationService? confirmation) => _confirmation = confirmation;

    /// <summary>宿主自身装配服务循环上下文；不受封口限制。</summary>
    internal void SetHostUiContext(SynchronizationContext? context) => _uiContext = context;

    private void EnsureConfigurable(string property)
    {
        if (_hostSealed)
        {
            throw new InvalidOperationException(
                $"宿主总线的 {property} 由宿主装配，模块不得改写；界面模块请使用 IModuleContext.RegisterFrontend。");
        }
    }

    /// <summary>本总线写回显、进度与结果的日志；宿主上下文经 <see cref="IModuleContext.Log"/> 交给模块。</summary>
    internal IShellLog Log => _log;

    /// <summary>这条命令是否整条交给 <see cref="RemoteExecutor"/>。</summary>
    private bool RoutesToRemote(CommandRequest request, string source)
        => _remoteExecutor != null && (_shouldUseRemoteCommand?.Invoke(request.Text, source) ?? true);

    private bool HasConfirmationChannel => Frontend != null || _confirmation != null;

    private bool Confirm(string prompt)
    {
        var frontend = Frontend;
        return frontend != null
            ? frontend.Confirm(prompt)
            : _confirmation?.Confirm(prompt) ?? false;
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
        if (RoutesToRemote(request, source))
            return await _remoteExecutor!(request.Text, source, cancellation).ConfigureAwait(false);

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
            if (!HasConfirmationChannel)
                return CommandResult.Fail("该指令需要二次确认,但当前环境没有确认通道,已拒绝执行");
            if (!Confirm(prompt))
                return CommandResult.Fail("已取消(用户未确认)");
        }

        // 执行(必要时编组 UI 线程)
        try
        {
            var ui = UiContext;
            if (descriptor.RequiresUiThread && ui != null && SynchronizationContext.Current != ui)
                return await OnUiThreadAsync(ui, () => descriptor.Handler(context)).ConfigureAwait(false);

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

    private static Task<CommandResult> OnUiThreadAsync(
        SynchronizationContext ui,
        Func<Task<CommandResult>> action)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(
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

    /// <summary>一次前端登记；释放时只撤销自己，不误伤同 owner 的后继登记。</summary>
    private sealed class FrontendRegistration(CommandBus bus, string owner, IFrontend frontend) : IDisposable
    {
        private int _released;

        public string Owner { get; } = owner;

        public IFrontend Frontend { get; } = frontend;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (bus._frontendGate)
            {
                if (ReferenceEquals(bus._frontend, this))
                    bus._frontend = null;
            }
        }
    }
}

/// <summary>
/// 二次确认通道(§5.2 拦截器链的首个内置拦截器;T-08 / R-06 等危险操作依赖)。
/// 界面以模态对话框实现;无 UI 场景(脚本/测试)可注入自动拒绝或自动通过的实现。
/// </summary>
public interface IConfirmationService
{
    /// <summary>返回 true 表示用户确认继续。</summary>
    bool Confirm(string prompt);
}
