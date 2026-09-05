using System.Diagnostics;
using System.IO;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.ServiceHost;

/// <summary>无界面的用户会话服务宿主，负责消息循环与通用模块装配。</summary>
public static class ServiceHost
{
    internal static void InstallDefaultConfirmation(ServiceComposition composition)
    {
        composition.Bus.Confirmation = new DenyConfirmation(composition.Log);
        composition.Bus.ConfirmationRouter = (_, prompt) => composition.Bus.Confirmation?.Confirm(prompt) ?? false;
    }

    private static readonly TimeSpan RestartMutexWait = TimeSpan.FromSeconds(10);

    public static int Run(
        ServiceComposition composition,
        string? executablePath = null,
        IReadOnlyList<string>? serviceArguments = null)
    {
        ArgumentNullException.ThrowIfNull(composition);
        var mutexName = $"Local\\{Sanitize(composition.ServiceName)}.ServiceHost";
        using var mutex = new Mutex(initiallyOwned: false, mutexName);
        if (!WaitForSingleInstance(mutex, RestartMutexWait))
            return 2;

        using var loop = new ServiceRunLoop();
        SynchronizationContext.SetSynchronizationContext(new ServiceRunLoopSynchronizationContext(loop));
        composition.Bus.UiContext = SynchronizationContext.Current;
        if (composition.Modules != null)
            composition.Modules.UiContext = SynchronizationContext.Current;

        var servicePath = executablePath
                          ?? Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("无法确定服务可执行文件路径");
        serviceArguments ??= [];

        InstallDefaultConfirmation(composition);
        // 服务指令已在 Build 时注册（4.5.0），这里只把「停机」这件唯一做不到的事接上。
        // 排到循环上再关：命令处理器跑在线程池上，同步关停会让循环在响应写回之前就排空退出。
        composition.RequestStop = () => loop.Post(() => loop.Shutdown());

        // Complete initial module discovery before exposing the runtime CLI.
        try
        {
            composition.Modules?.Attach(composition.Registry);
            composition.Modules?.Start();
        }
        catch (Exception ex)
        {
            composition.Log.Warn("module", $"模块启动失败，可通过本地 CLI 查询和恢复: {ex.Message}");
        }

        // Runtime CLI 是本机、当前用户 ACL 的受限控制通道；它只连到这个实际服务循环，
        // 不会把离线组合根误报成运行时热重载。
        using var runtimePipe = RuntimePipeServer.Start(
            composition,
            HistoryVulcan.Core.AppIdentity.Current.Name,
            HistoryVulcan.Core.AppIdentity.Current.Version);

        if (composition.RegisterAutostartOnFirstRun && composition.Autostart != null)
        {
            try
            {
                var preference = composition.Settings.Get("svc.autostart");
                var enabled = preference is null
                              || !bool.TryParse(preference, out var parsed)
                              || parsed;
                if (!enabled)
                {
                    composition.Autostart.SetEnabled(
                        composition.ServiceName, servicePath, serviceArguments, enabled: false);
                }
                else if (!composition.Autostart.IsEnabled(
                             composition.ServiceName, servicePath, serviceArguments))
                {
                    composition.Autostart.SetEnabled(
                        composition.ServiceName, servicePath, serviceArguments, enabled: true);
                }
            }
            catch (Exception ex)
            {
                composition.Log.Warn("svc", $"注册登录启动失败: {ex.Message}");
            }
        }

        // 原先排在 DispatcherPriority.ApplicationIdle，意图是"排在已入队的启动工作之后"。
        // FIFO 队列天然满足该次序，无需优先级概念。
        loop.Post(() =>
        {
            foreach (var work in composition.DeferredWork)
                _ = Task.Run(() => RunDeferredAsync(work, composition.Log));
        });

        try
        {
            return loop.Run(ex => composition.Log.Error("svc", $"服务循环回调异常: {ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            runtimePipe.Dispose();
            composition.Dispose();
            mutex.ReleaseMutex();
        }
    }

    private static async Task RunDeferredAsync(HistoryVulcan.Core.Modules.IDeferredStartupWork work, IShellLog log)
    {
        try
        {
            await work.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Warn("startup", $"延迟启动任务 {work.GetType().Name} 失败: {ex.Message}");
        }
    }

    private static string Sanitize(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));

    private static bool WaitForSingleInstance(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

}
