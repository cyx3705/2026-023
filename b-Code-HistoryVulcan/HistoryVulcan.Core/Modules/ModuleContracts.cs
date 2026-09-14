// 模块契约：注册器装载模块时给什么、模块回给注册器什么。
//
// 5.0 起宿主不再向模块注入设置、日志、数据根或界面抽象。
// 模块只拿到命令总线、把指令暂存进当前快照的登记口，以及（5.4 起）登记唯一前端的入口。
// 5.5 起另给宿主那唯一一份日志：控制台只显示它，界面不再自建第二份。
// 身份仍是 BaseVariable.ModuleInfoBase 的全名鸭子类型（MD-02）。

using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Core.Modules;

/// <summary>注册器在装载时交给模块的唯一运行时入口：总线、指令登记与前端登记。</summary>
public interface IModuleContext
{
    /// <summary>当前宿主进程拥有的那一条命令总线。宿主装配的开关在其上只读。</summary>
    CommandBus Bus { get; }

    /// <summary>
    /// 宿主那唯一一份日志（5.5.0）：<see cref="Bus"/> 上每条指令的回显、进度与结果都写在这里，并落宿主日志文件。
    /// </summary>
    /// <remarks>
    /// 控制台应当显示这一份，而不是在界面里另建日志：经总线执行的指令——不论来自界面、CLI、MCP
    /// 还是模块的嵌套调用——过程只会出现在这里。模块自己的运行日志也可以写进来，与指令日志同处可查。
    ///
    /// 带默认实现是刻意的：模块 Smoke 里自写的 IModuleContext 测试替身不必为此改动，
    /// 未覆盖本成员的替身被读取时抛 <see cref="NotSupportedException"/>。宿主提供的上下文总是覆盖它。
    /// </remarks>
    IShellLog Log
        => throw new NotSupportedException("当前 IModuleContext 实现不提供宿主日志；只有宿主提供的上下文提供。");

    /// <summary>
    /// 把本模块指令暂存进当前快照。传入的注册表与活注册表隔离；
    /// 宿主在提交快照时一并登记，随模块卸载一并撤销。
    /// </summary>
    void RegisterCommands(Action<CommandRegistry> configure);

    /// <summary>
    /// 把本模块登记为宿主唯一的前端：接管二次确认、界面线程编组与界面生命周期命令中继。
    /// </summary>
    /// <remarks>
    /// 5.4 起取代直接改写总线的 FrontendExecutor / Confirmation / ConfirmationRouter / UiContext。
    /// 同一宿主只允许一个前端：另一模块已登记时抛 <see cref="InvalidOperationException"/>；
    /// 同一模块重复登记会替换旧登记。释放返回值或模块被卸载时撤销，确认回到宿主缺省（拒绝）。
    ///
    /// 带默认实现是刻意的：模块 Smoke 里自写的 IModuleContext 测试替身不必为此改动，
    /// 未覆盖本成员的替身被调用时抛 <see cref="NotSupportedException"/>。宿主提供的上下文总是覆盖它。
    /// </remarks>
    /// <param name="frontend">前端实现；由登记方持有其生命周期。</param>
    /// <returns>撤销本次登记的句柄；重复释放无副作用。</returns>
    IDisposable RegisterFrontend(IFrontend frontend)
        => throw new NotSupportedException("当前 IModuleContext 实现不支持前端登记；只有宿主提供的上下文支持。");
}

/// <summary>需要登记指令或使用总线的模块实现本接口，由注册器在激活指令前调用。</summary>
public interface IModuleContextAware
{
    /// <summary>挂上当前宿主上下文。</summary>
    void Attach(IModuleContext context);
}

/// <summary>
/// 界面模块经 <see cref="IModuleContext.RegisterFrontend"/> 交给宿主的前端能力。
/// </summary>
/// <remarks>
/// 宿主只在三件事上需要界面：向坐在屏幕前的人确认、把声明了界面线程的指令编组过去、
/// 以及把 <c>vulcan.app.show / hide / close / focusconsole</c> 转交出去。三件都收在这里，
/// 不再以可被任意模块改写的总线属性存在。
/// </remarks>
public interface IFrontend
{
    /// <summary>界面线程上下文；声明 <see cref="CommandDescriptor.RequiresUiThread"/> 的指令在其上执行。</summary>
    SynchronizationContext UiContext { get; }

    /// <summary>向用户询问是否继续。可能在任意线程被调用，实现负责编组到界面线程。</summary>
    /// <param name="prompt">面向用户的确认文案，已由总线脱敏。</param>
    /// <returns>用户确认继续时为 true。</returns>
    bool Confirm(string prompt);

    /// <summary>执行宿主转交的界面生命周期命令。</summary>
    /// <param name="commandName">宿主生命周期命令名，例如 <c>vulcan.app.show</c>。</param>
    /// <param name="source">原始调用来源标签。</param>
    /// <param name="cancellation">调用方的取消令牌。</param>
    /// <returns>界面侧的执行回执。</returns>
    Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation);
}

/// <summary>为反射模块方法补充命令元数据。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModuleCommandAttribute : Attribute
{
    /// <summary>方法不改变任何状态；为 true 时可被只读消费面（例如 MCP 投影）调用。</summary>
    public bool Readonly { get; init; }

    /// <summary>命令在当前模块域内的功能类；未声明时归入 core。</summary>
    public string? CommandClass { get; init; }
}
