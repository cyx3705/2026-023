// 模块契约：宿主提供给模块的运行时服务，以及模块回给宿主的三个挂钩。
//
// IModuleContext / IModuleContextAware —— 装载时注入的总线、设置、日志与数据根。
// IShellUiProvider / IShellUiRegistrar —— 界面注册器，由承载界面的模块反向提供。
// IUiModule 及其邻居 —— 界面模块的生命周期。
//
// 三组都是**同一件事的两端**：宿主装载模块时给什么、模块回给宿主什么。
// 分成三个文件读的时候要来回跳，而它们从来是一起改的。
//
// 模块身份 ModuleInfoBase 不在此处：它属于 BaseVariable 命名空间，
// 宿主按**全名**鸭子类型识别，与本文件的契约是两套机制（MD-02）。

using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Core.Modules;

/// <summary>Host-owned runtime services made available to an HistoryVulcan module.</summary>
public interface IModuleContext
{
    /// <summary>The single command bus owned by the current host process.</summary>
    CommandBus Bus { get; }

    /// <summary>The settings store owned by the current host process.</summary>
    ISettingsService Settings { get; }

    /// <summary>The log owned by the current host process.</summary>
    IShellLog Log { get; }

    /// <summary>The data root owned by the current host process.</summary>
    string DataDirectory { get; }

    /// <summary>
    /// Stages module commands for the current module snapshot. The supplied registry is isolated
    /// from the live host registry; the host commits and removes its contents with the module.
    /// </summary>
    void RegisterCommands(Action<CommandRegistry> configure);
}

/// <summary>Implemented by modules that consume host-owned runtime services.</summary>
public interface IModuleContextAware
{
    /// <summary>Attaches the current host context before commands and UI are activated.</summary>
    void Attach(IModuleContext context);
}

/// <summary>模块向宿主注册内嵌界面的唯一门面。</summary>
public interface IShellUiRegistrar
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    bool IsUiThread { get; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void Invoke(Action action);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterToolWindow(string id);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void UnregisterOwner(string owner);
}

/// <summary>
/// 由模块**反向提供**界面注册入口。
///
/// 与 <see cref="IShellUiAware"/> 方向相反：那个是宿主把注册器注入给消费方，
/// 本接口是承载界面的那个模块把注册器交给宿主，宿主再转给其余消费方。
/// 界面整体成为模块之后（Aurora DEC-008），宿主自己不再持有任何界面实现，
/// 注册器只能来自模块。
///
/// 宿主在 <c>CreateUi</c> 阶段先取遍提供方再分发给消费方——模块装载顺序不可控，
/// 靠目录名排序碰巧让提供方排在前面是不能依赖的。
/// </summary>
public interface IShellUiProvider
{
    /// <summary>本模块提供的界面注册入口；界面尚未就绪时为 null。</summary>
    IShellUiRegistrar? ShellUi { get; }
}

/// <summary>由服务宿主 UI 线程创建和销毁的模块能力。</summary>
public interface IUiModule
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void CreateUi();

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    void DestroyUi();
}

/// <summary>模块实现本接口后，宿主会在 CreateUi 前注入界面注册器。</summary>
public interface IShellUiAware
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    IShellUiRegistrar ShellUi { set; }
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

/// <summary>Optional content contract used when a tool window is shown and should receive input focus.</summary>
public interface IActivatableToolContent
{
    /// <summary>Activates the content's primary interaction target.</summary>
    void ActivateContent();
}

/// <summary>网关就绪后执行的非关键启动工作；失败不得阻断宿主。</summary>
public interface IDeferredStartupWork
{
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    Task ExecuteAsync(CancellationToken cancellationToken);
}
