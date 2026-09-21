using System.IO;
using System.Text.Json;
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

            if (!result.Success)
                return CommandResult.Fail($"Vulcan 热重载失败：{result.Message}");

            var installed = $"已热重载到活宿主。\n来源: {packagePath}\n{result.Message}";
            var attach = await VerifyAttachedAsync(host, packagePath, source, cancellation).ConfigureAwait(false);
            return attach.Success
                ? CommandResult.Ok(installed + "\n" + attach.Message, attach.Data ?? result.Data)
                : CommandResult.Fail(installed + "\n" + attach.Message);
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

    /// <summary>宿主日志位置，附着失败时指给人看。原因全文只在那里。</summary>
    internal const string HostLogHint =
        @"%AppData%\HistoryVulcan\service\logs\shell-<日期>.log 里的 [module.discovery]";

    /// <summary>
    /// 5.7.0（U3）：装完再问一次活宿主，这个模块到底接上没有。
    ///
    /// <c>vulcan.module.install</c> 成功只证明包装进去了。曾出现回执成功、模块却 <c>attached=false</c>
    /// （PendingAttach 重复）的情况，使用者因此每次都要另外核对。这里把核对并进管线：
    /// 没接上、版本不是候选版本、或带着附着失败，一律算失败。
    /// 问不到（没有 module.list、读不出身份、形状认不出）时不冒充成功也不冒充失败，照实写「未核对」。
    /// </summary>
    internal static async Task<CommandResult> VerifyAttachedAsync(
        DevelopmentContext host,
        string packagePath,
        string source,
        CancellationToken cancellation)
    {
        if (!TryReadIdentity(packagePath, out var name, out var version))
            return CommandResult.Ok("附着状态未核对：读不出候选包 module.manifest.json 的 name/version。");

        ModuleAttachState? state = null;
        string? queryError = null;
        var recognized = false;
        // 装包与附着在活宿主上是同一轮完成的；短暂重试只为吸收「列表还没换成新快照」的那一拍。
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(300, cancellation).ConfigureAwait(false);

            var listed = host.LiveHost is not null
                ? await host.LiveHost("vulcan.module.list", cancellation).ConfigureAwait(false)
                : await host.Bus.ExecuteAsync("vulcan.module.list", source, cancellation).ConfigureAwait(false);
            if (!listed.Success)
            {
                queryError = listed.Message;
                break;
            }

            recognized = ModuleAttachState.TryFind(listed.Data, name, out state);
            if (!recognized)
                break;
            if (state is { AttachFailures.Count: > 0 }
                || (state is not null && string.Equals(state.Version, version, StringComparison.OrdinalIgnoreCase)))
                break;
        }

        if (queryError is not null)
            return CommandResult.Ok($"附着状态未核对：vulcan.module.list 失败（{queryError}）。");
        if (!recognized)
            return CommandResult.Ok("附着状态未核对：vulcan.module.list 没有返回可识别的模块列表。");
        if (state is null)
        {
            return CommandResult.Fail(
                $"装包后活宿主的模块列表里没有 {name}；模块没有接上。原因见宿主日志 {HostLogHint}。");
        }

        var data = new
        {
            Module = name,
            state.Version,
            ExpectedVersion = version,
            state.Attached,
            state.CommandCount,
            state.AttachFailures,
        };
        if (!state.Attached || state.AttachFailures.Count > 0)
        {
            var first = state.AttachFailures.Count > 0 ? state.AttachFailures[0] : "（宿主未给出原因）";
            return CommandResult.Fail(
                $"模块没有接上宿主：{name} {state.Version} attached=false，指令未注册。\n首条原因：{first}\n"
                + $"全文见宿主日志 {HostLogHint}。");
        }

        if (!string.Equals(state.Version, version, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail(
                $"活宿主上的 {name} 是 {state.Version}，不是候选版本 {version}；新包没有生效。"
                + $"原因见宿主日志 {HostLogHint}。");
        }

        return new CommandResult
        {
            Success = true,
            Message = $"已核对：{name} {state.Version} 已接上宿主，{state.CommandCount} 条指令。",
            Data = data,
        };
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

    private static bool TryReadIdentity(string packagePath, out string name, out string version)
    {
        name = "";
        version = "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(packagePath, "module.manifest.json")));
            name = document.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            version = document.RootElement.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
            return name.Length > 0 && version.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// <c>vulcan.module.list</c> 里一个模块的附着读数。
/// 同进程拿到的是 <c>ModuleMeta</c> 列表，经 --runtime 管道回来的是驼峰 JSON；两种都统一成 JSON 再按名字读。
/// </summary>
internal sealed record ModuleAttachState(
    string Version,
    bool Attached,
    int CommandCount,
    IReadOnlyList<string> AttachFailures)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// 在模块列表里找 <paramref name="moduleName"/>。返回 false 表示**认不出这是一张模块列表**；
    /// 返回 true 而 <paramref name="state"/> 为空，才是「列表里确实没有它」。两者后果不同，不能混成一个 null。
    /// </summary>
    internal static bool TryFind(object? data, string moduleName, out ModuleAttachState? state)
    {
        state = null;
        if (data is null)
            return false;
        JsonElement list;
        try
        {
            list = data is JsonElement element ? element : JsonSerializer.SerializeToElement(data, Web);
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return false;
        }

        if (list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !string.Equals(Text(item, "moduleName") ?? Text(item, "name"), moduleName, StringComparison.OrdinalIgnoreCase))
                continue;

            var failures = Property(item, "attachFailures") is { ValueKind: JsonValueKind.Array } array
                ? array.EnumerateArray().Select(entry => entry.ToString()).ToList()
                : [];
            var attached = Property(item, "attached") is { } flag && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean()
                : failures.Count == 0;
            var count = Property(item, "commandCount") is { ValueKind: JsonValueKind.Number } number
                        && number.TryGetInt32(out var parsed)
                ? parsed
                : 0;
            state = new ModuleAttachState(Text(item, "version") ?? "", attached, count, failures);
            return true;
        }

        return true;
    }

    private static JsonElement? Property(JsonElement item, string name)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }
        return null;
    }

    private static string? Text(JsonElement item, string name)
        => Property(item, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
