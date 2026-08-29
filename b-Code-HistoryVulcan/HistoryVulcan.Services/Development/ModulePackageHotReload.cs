using System.IO;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.Services.Development;

/// <summary>
/// Module test and deploy both call Vulcan's package hot-reload:
/// <c>vulcan.module.install</c>. That is the same path as the Modules page 热重载 button.
/// </summary>
internal static class ModulePackageHotReload
{
    internal static async Task<CommandResult> InstallAsync(
        DevelopmentContext host,
        string path,
        string source,
        CancellationToken cancellation)
    {
        string packagePath;
        try
        {
            packagePath = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CommandResult.Fail($"发布包路径无效：{ex.Message}");
        }

        if (!Directory.Exists(packagePath))
            return CommandResult.Fail($"发布包目录不存在：{packagePath}");

        var command = $"vulcan.module.install path={CommandParser.QuoteArg(packagePath)}";
        try
        {
            CommandResult result;
            if (host.LiveHost is not null)
            {
                result = await host.LiveHost(command, cancellation).ConfigureAwait(false);
            }
            else
            {
                result = await host.Bus.ExecuteAsync(command, source, cancellation)
                    .ConfigureAwait(false);
                if (result.Success
                    && (result.Message.Contains("不装载 UI 模块", StringComparison.Ordinal)
                        || result.Message.Contains("不是活宿主热重载", StringComparison.Ordinal)))
                {
                    return CommandResult.Fail(
                        "离线 CLI 只写了磁盘，没有重载活宿主。"
                        + " HistoryVulcan.Cli.exe --cli 必须把装包打到正在跑的宿主；活宿主不可达时不要把写入运行区说成热重载。"
                        + "\n" + result.Message);
                }
            }

            return result.Success
                ? CommandResult.Ok(
                    $"已热重载到活宿主。\n来源: {packagePath}\n{result.Message}",
                    result.Data)
                : CommandResult.Fail($"Vulcan 热重载失败：{result.Message}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"Vulcan 热重载异常 {ex.GetType().Name}: {ex.Message}");
        }
    }

    internal static async Task<CommandResult> InstallCurrentAsync(
        DevelopmentContext host,
        string projectRoot,
        string moduleName,
        string source,
        CancellationToken cancellation)
    {
        string snapshot;
        try
        {
            snapshot = PublishPackages.ResolveCurrent(projectRoot, moduleName);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return CommandResult.Fail(ex.Message);
        }

        return await InstallAsync(host, snapshot, source, cancellation).ConfigureAwait(false);
    }
}
