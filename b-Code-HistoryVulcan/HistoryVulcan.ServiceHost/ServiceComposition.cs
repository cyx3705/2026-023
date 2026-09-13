using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Development;

namespace HistoryVulcan.ServiceHost;

/// <summary>服务进程的通用组合根；框架不包含任何派生应用领域对象。</summary>
internal sealed class ServiceComposition : IDisposable
{
    public required string ServiceName { get; init; }

    public required CommandRegistry Registry { get; init; }

    public required CommandBus Bus { get; init; }

    public required ISettingsService Settings { get; init; }

    public required IShellLog Log { get; init; }

    public ModuleHost? Modules { get; init; }

    internal DevelopmentContext? Development { get; init; }

    /// <summary>
    /// 应用数据根，脚本等相对路径以它为基准。
    ///
    /// 必须是 <c>%AppData%\HistoryVulcan</c> 本身而不是其下的 <c>service</c> 子目录：
    /// <c>vulcan.command.run</c> 从前端搬到服务侧时，用户已有脚本的相对路径
    /// 解析基准不能改变，否则所有相对路径脚本会在升级后集体找不到文件。
    /// </summary>
    public string? DataDirectory { get; init; }

    public bool RegisterAutostartOnFirstRun { get; init; }

    public IAutostartManager? Autostart { get; init; }

    public Action? DisposeApplicationServices { get; init; }

    /// <summary>
    /// 请求停止服务循环。仅在 <see cref="ServiceHost.Run"/> 里被赋值。
    /// </summary>
    /// <remarks>
    /// 服务指令（<c>vulcan.svc.stop</c> / <c>vulcan.app.quit</c> / <c>vulcan.svc.restart</c>）
    /// 随 4.5.0 从 <c>Run</c> 移进 <c>Build</c>，好让命令行入口也能看见它们。
    /// 但「停机」这件事只有真正跑着循环的那个进程做得到，因此挂钩留空是**常态**而不是异常：
    /// <c>--cli</c> 进程没有循环，那几条指令会明确失败，而不是假装停了一个不存在的服务。
    ///
    /// 语义上自带「排到循环上再关」：命令处理器跑在线程池上，同步关停会让循环
    /// 在响应写回之前就排空退出。
    /// </remarks>
    public Action? RequestStop { get; set; }

    public void Dispose()
    {
        Modules?.Dispose();
        DisposeApplicationServices?.Invoke();
        if (Log is IDisposable disposable)
            disposable.Dispose();
    }
}
