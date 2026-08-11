using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Services.Web;

namespace HistoryVulcan.ServiceHost;

/// <summary>无主窗口的用户会话 WPF 服务宿主。</summary>
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

        var app = Application.Current ?? new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        composition.Bus.UiContext = SynchronizationContext.Current;
        if (composition.Modules != null)
            composition.Modules.UiContext = SynchronizationContext.Current;

        var servicePath = executablePath
                          ?? Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("无法确定服务可执行文件路径");
        serviceArguments ??= [];

        var confirmation = new ServiceConfirmation();
        var gatewayAwareConfirmation = new GatewayAwareConfirmation(confirmation);
        composition.Bus.Confirmation = gatewayAwareConfirmation;
        composition.Bus.ConfirmationRouter = (context, prompt) =>
        {
            if (composition.Web?.TryGetSession(context.Source, out var session) == true
                && !session.IsLoopback)
            {
                if (!session.Scopes.Contains("operate") && !session.Scopes.Contains("admin"))
                    return false;
                if (!string.Equals(
                        composition.Settings.Get("lan.confirm"),
                        "client",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                return composition.Web.RequestWebConfirmation(
                    prompt, context.Source, TimeSpan.FromSeconds(60));
            }
            if (!context.Source.StartsWith("Web:", StringComparison.OrdinalIgnoreCase))
                return gatewayAwareConfirmation.Confirm(prompt);
            if (!string.Equals(
                    composition.Settings.Get(WebGateway.KeyConfirm),
                    "web",
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return composition.Web?.RequestWebConfirmation(
                prompt,
                context.Source,
                TimeSpan.FromSeconds(60)) == true;
        };
        ServiceCommands.RegisterAll(
            composition.Registry,
            composition,
            () => app.Shutdown(),
            servicePath,
            serviceArguments: serviceArguments);

        // The module registry is authoritative for both Web and MCP. Complete the first
        // synchronous load before either listener is opened so the first remote catalog
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

        if (composition.Web != null)
        {
            composition.Bus.FrontendExecutor = composition.Web.RelayFrontendCommandAsync;
            var (started, message) = composition.Web.Start();
            LogResult(composition.Log, "web", started, message);
            if (started && composition.EndpointFile != null)
                WriteEndpoint(composition.EndpointFile, composition);
        }

        if (composition.Mcp != null)
        {
            var (started, message) = composition.Mcp.TryAutostart();
            LogResult(composition.Log, "mcp", started, message);
        }

        if (composition.RegisterAutostartOnFirstRun && composition.Autostart != null)
        {
            try
            {
                if (!composition.Autostart.IsEnabled(
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

        app.Dispatcher.BeginInvoke(() =>
        {
            foreach (var work in composition.DeferredWork)
                _ = Task.Run(() => RunDeferredAsync(work, composition.Log));
        }, DispatcherPriority.ApplicationIdle);

        try
        {
            return app.Run();
        }
        finally
        {
            if (composition.EndpointFile != null)
            {
                try { File.Delete(composition.EndpointFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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

    private static void WriteEndpoint(string path, ServiceComposition composition)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                port = composition.Web?.Port ?? 0,
                serverId = composition.Web?.ServerId ?? composition.ServiceName,
                processId = Environment.ProcessId,
            }));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
