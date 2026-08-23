// 模块契约：注册器装载模块时给什么、模块回给注册器什么。
//
// 5.0 起宿主不再向模块注入设置、日志、数据根或界面抽象。
// 模块只拿到命令总线，以及把指令暂存进当前快照的登记口。
// 身份仍是 BaseVariable.ModuleInfoBase 的全名鸭子类型（MD-02）。

using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Core.Modules;

/// <summary>注册器在装载时交给模块的唯一运行时入口：总线与指令登记。</summary>
public interface IModuleContext
{
    /// <summary>当前宿主进程拥有的那一条命令总线。</summary>
    CommandBus Bus { get; }

    /// <summary>
    /// 把本模块指令暂存进当前快照。传入的注册表与活注册表隔离；
    /// 宿主在提交快照时一并登记，随模块卸载一并撤销。
    /// </summary>
    void RegisterCommands(Action<CommandRegistry> configure);
}

/// <summary>需要登记指令或使用总线的模块实现本接口，由注册器在激活指令前调用。</summary>
public interface IModuleContextAware
{
    /// <summary>挂上当前宿主上下文。</summary>
    void Attach(IModuleContext context);
}

/// <summary>为反射模块方法补充命令元数据。</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ModuleCommandAttribute : Attribute
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool Readonly { get; init; }

    /// <summary>命令在当前模块域内的功能类；未声明时归入 core。</summary>
    public string? CommandClass { get; init; }
}

/// <summary>网关就绪后执行的非关键启动工作；失败不得阻断宿主。</summary>
public interface IDeferredStartupWork
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
