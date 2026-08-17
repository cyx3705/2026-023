using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Modules;

namespace HistoryVulcan.App;

public partial class App
{
    internal static async Task<CommandResult> InstallRuntimePackageAsync(
        ModuleHost host,
        CommandBus bus,
        string path,
        CancellationToken cancellationToken = default)
    {
        var moduleName = TryReadPackageName(path);
        var frontend = await UnloadFrontendModuleAsync(
            bus,
            moduleName,
            cancellationToken).ConfigureAwait(false);
        var result = await Task.Run(() => host.InstallPackage(path))
            .ConfigureAwait(false);
        return RestoreFrontendAfterFailedMutation(host, result, frontend);
    }

    internal static async Task<CommandResult> RemoveRuntimePackageAsync(
        ModuleHost host,
        CommandBus bus,
        string name,
        CancellationToken cancellationToken = default)
    {
        var frontend = await UnloadFrontendModuleAsync(
            bus,
            name,
            cancellationToken).ConfigureAwait(false);
        var result = await Task.Run(() => host.RemovePackage(name))
            .ConfigureAwait(false);
        return RestoreFrontendAfterFailedMutation(host, result, frontend);
    }

    private static async Task<FrontendUnloadResult> UnloadFrontendModuleAsync(
        CommandBus bus,
        string? moduleName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(moduleName)
            || bus.FrontendExecutor is not { } frontend)
            return new FrontendUnloadResult(false, null);

        var result = await frontend(
            $"vulcan.module.unload name={CommandParser.QuoteArg(moduleName)}",
            "framework:service",
            cancellationToken).ConfigureAwait(false);
        return new FrontendUnloadResult(result.Success, result.Message);
    }

    private static CommandResult RestoreFrontendAfterFailedMutation(
        ModuleHost host,
        CommandResult result,
        FrontendUnloadResult frontend)
    {
        if (result.Success || !frontend.Unloaded)
            return result;

        // The backend transaction can fail before ModuleHost publishes a revision (for example
        // duplicate or malformed runtime contents). Ensure the UI reloads its previous package.
        try { host.Reload(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return CommandResult.Fail($"{result.Message}；前端恢复重载失败: {ex.Message}");
        }

        return result;
    }

    private static string? TryReadPackageName(string path)
    {
        try
        {
            var manifest = Path.Combine(Path.GetFullPath(path.Trim()), "module.manifest.json");
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!document.RootElement.TryGetProperty("name", out var name)
                || name.ValueKind != JsonValueKind.String)
                return null;
            var value = name.GetString()?.Trim();
            return value is { Length: > 0 }
                   && value.Equals(Path.GetFileName(value), StringComparison.Ordinal)
                ? value
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private readonly record struct FrontendUnloadResult(bool Unloaded, string? Message);
}
