using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Mcp;
using HistoryVulcan.Services.Modules;
using HistoryVulcan.Services.Web;

namespace HistoryVulcan.ServiceHost;

/// <summary>服务进程的通用组合根；框架不包含任何派生应用领域对象。</summary>
public sealed class ServiceComposition : IDisposable
{
    public required string ServiceName { get; init; }

    public required CommandRegistry Registry { get; init; }

    public required CommandBus Bus { get; init; }

    public required ISettingsService Settings { get; init; }

    public required IShellLog Log { get; init; }

    public ModuleHost? Modules { get; init; }


    public McpGateway? Mcp { get; init; }

    public WebGateway? Web { get; init; }

    /// <summary>Optional loopback endpoint file used by a cooperating desktop frontend.</summary>
    public string? EndpointFile { get; init; }

    /// <summary>
    /// 应用数据根，脚本等相对路径以它为基准。
    ///
    /// 必须是 <c>%AppData%\HistoryVulcan</c> 本身而不是其下的 <c>service</c> 子目录：
    /// <c>vulcan.command.run</c> 从前端搬到服务侧（REQ-A6）时，用户已有脚本的相对路径
    /// 解析基准不能改变，否则所有相对路径脚本会在升级后集体找不到文件。
    /// </summary>
    public string? DataDirectory { get; init; }

    public IReadOnlyList<IDeferredStartupWork> DeferredWork { get; init; } = [];

    public bool RegisterAutostartOnFirstRun { get; init; }

    public IAutostartManager? Autostart { get; init; }

    public Action? DisposeApplicationServices { get; init; }

    public void Dispose()
    {
        Web?.Dispose();
        Mcp?.Dispose();
        Modules?.Dispose();
        DisposeApplicationServices?.Invoke();
        if (Log is IDisposable disposable)
            disposable.Dispose();
    }
}
