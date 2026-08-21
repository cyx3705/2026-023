using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;

namespace HistoryVulcan.ServiceHost;

/// <summary>无界面的用户会话服务宿主（4.0.0 起不再依赖 WPF，见 REQ-A4）。</summary>
public static class ServiceHost
{
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

        // 4.0.0（REQ-A2）：确认中继到前端，服务进程不再自己弹框。
        // 前端未连接时拒绝而非放行——没有人可问就等于没得到批准。
        var confirmation = new ShellRelayConfirmation(composition.Log);
        var gatewayAwareConfirmation = new GatewayAwareConfirmation(confirmation);
        composition.Bus.Confirmation = gatewayAwareConfirmation;
        // 3.13.0 删除局域网面后，网关只接受同机前端 Shell（源形如 "Shell:v1.…"），
        // 因此原先的两条远程分支都已不可达，一并退役：
        //   - 非回环会话按 scope + lan.confirm 决定是否回问客户端。`lan.confirm` 这个键
        //     从来没有任何代码写入过（唯一能写确认档的 WebCommands 写的是 web.confirm，
        //     且它自己从未被注册），所以这条分支在任何配置下都只会返回 false。
        // 剩下的唯一语义就是本机确认。
        composition.Bus.ConfirmationRouter =
            (_, prompt) => gatewayAwareConfirmation.Confirm(prompt);
        ServiceCommands.RegisterAll(
            composition.Registry,
            composition,
            // requestStop 自带"排到循环上再关"的语义：命令处理器跑在线程池上，
            // 同步关停会让循环在响应写回之前就排空退出。
            () => loop.Post(() => loop.Shutdown()),
            servicePath,
            serviceArguments: serviceArguments);

        // The module registry is authoritative for every gateway. Complete the first
        // synchronous load before any listener is opened so the first remote catalog
        // cannot observe a framework-only intermediate snapshot.
        try
        {
            composition.Modules?.Attach(composition.Registry);
            composition.Modules?.Start();
        }
        catch (Exception ex)
        {
            composition.Log.Warn("module", $"模块启动失败，远程网关将仅暴露成功注册的指令: {ex.Message}");
        }

        // Web 网关与 endpoint.json 已迁出至 HistoryPortunus 模块（4.3.0）。
        // 宿主不再持有任何对外 HTTP 监听：模块没装上时本机就没有 Web 入口，
        // 这一点由 endpoint.json 的存在与否如实反映。
        if (composition.Mcp != null)
        {
            var (started, message) = composition.Mcp.TryAutostart();
            LogResult(composition.Log, "mcp", started, message);
        }

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
            // endpoint.json 的删除随模块拆除发生（ModuleHost 在卸载前 Dispose 模块实例）。
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

    private static void LogResult(IShellLog log, string category, bool success, string message)
    {
        if (success)
            log.Info(category, message);
        else
            log.Warn(category, message);
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
