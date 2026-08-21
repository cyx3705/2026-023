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

        try
        {
            var result = await host.Bus.ExecuteAsync(
                    $"vulcan.module.install path={CommandParser.QuoteArg(packagePath)}",
                    source,
                    cancellation)
                .ConfigureAwait(false);
            return result.Success
                ? CommandResult.Ok(
                    $"已热重载到 Vulcan 运行区。\n来源: {packagePath}\n{result.Message}",
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
