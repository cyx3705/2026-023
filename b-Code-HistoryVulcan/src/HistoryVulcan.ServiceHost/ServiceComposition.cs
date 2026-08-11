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
