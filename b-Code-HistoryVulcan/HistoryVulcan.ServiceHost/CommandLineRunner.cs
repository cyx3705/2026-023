using System.Reflection;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 命令行入口：在本进程内执行一条已声明暴露的指令，打印结果后退出。
/// </summary>
/// <remarks>
/// <c>--cli</c> 自己装配一份离线组合根、执行一条指令、退出。这不是省事，是恢复通道
/// 不能依赖「某个模块已经装载成功」或「活宿主已经起来」。
///
/// 走 MCP 要 agent 的会话活着，走 Web 要 HistoryPortunus 装载成功——而需要用命令行的时刻，
/// 恰恰是「某个模块坏了」或者「宿主起不来」。本进程内执行不需要端口、不需要令牌、
/// 不需要 endpoint.json。
///
/// 模块开发的 <c>vulcan.dev.submit</c> / <c>finish</c> 是例外：构建、写工作区 z、提交
/// 仍在离线组合里完成；装包必须再经当前用户命名管道打到<strong>正在跑的</strong>宿主
/// 执行 <c>vulcan.module.install</c>。不能把「本进程写了 AppData」说成热重载。
/// 活宿主不可达时这条指令失败，不降级。宿主起不来时的磁盘恢复仍走
/// <see cref="HistoryVulcan.Services.Modules.OfflineModuleInstall"/>。
///
/// 暴露面见 <see cref="CliExposurePolicy"/>：一份名单，恰好是开发管线与模块恢复。
/// 它是三个消费面里唯一不由描述符声明的——理由与那份名单的代价都写在该类注释里。
/// </remarks>
internal static class CommandLineRunner
{
    /// <summary>
    /// 执行一条指令。
    /// </summary>
    /// <returns>0 成功；1 指令执行失败；2 参数错误或未声明暴露。</returns>
    public static int Run(string commandText, Assembly identityAssembly)
        => Run(commandText, identityAssembly, HostOutputFormat.Human);

    /// <summary>按指定格式执行一条离线 CLI 指令。</summary>
    public static int Run(
        string commandText,
        Assembly identityAssembly,
        HostOutputFormat format)
        => Run(commandText, identityAssembly, format, () => ServiceComposer.Build(
            Environment.ProcessPath ?? identityAssembly.Location, identityAssembly));

    internal static int Run(
        string commandText, Assembly identityAssembly, HostOutputFormat format, Func<ServiceComposition> compose)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            WriteError($"原始命令: {commandText}\n需要一条指令文本。", format);
            return 2;
        }

        ServiceComposition? composition = null;
        try
        {
            // 白名单在创建组合和扫描模块前检查，错误输入不触发模块构造、Attach 或文件监听。
            var parsed = CommandParser.Parse(commandText);
            if (!CliExposurePolicy.IsExposed(parsed.Name))
            {
                WriteError(CliExposurePolicy.RefusalReason(parsed.Name)
                    + "；需要运行宿主时请改用 HistoryVulcan.Cli.exe --runtime。", format);
                return 2;
            }

            composition = compose();
            // 离线 CLI 不具备桌面消息循环；UI 模块即使命令面未使用也可能在 Attach
            // 阶段加载 WindowsDesktop 程序集，因此明确跳过它们。
            if (composition.Modules is not null)
            {
                composition.Modules.EnableUiModules = false;
                composition.Modules.EnableFileWatching = false;
            }
            if (composition.Development is { } pipeline)
            {
                pipeline.LiveHost = (command, cancellation) =>
                    RuntimeCommandClient.ExecutePipelineAsync(command, identityAssembly, cancellation);
            }

            // 全部 CLI 名称由宿主注册；先对账，再扫描包以提供离线模块与命令目录。
            var missing = CliExposurePolicy.MissingCommands(composition.Registry);
            if (missing.Count > 0)
            {
                WriteError(
                    $"原始命令: {commandText}\n命令行名单与注册表对不上，以下指令已不存在: " + string.Join("、", missing)
                    + "。请更新 CliExposurePolicy.ExposedCommands 后重试。", format);
                return 2;
            }

            if (!composition.Registry.TryGet(parsed.Name, out _))
            {
                WriteError($"原始命令: {commandText}\n未知指令: {parsed.Name}。请使用 --help 或 vulcan.cli.list。", format);
                return 2;
            }

            try
            {
                composition.Modules?.Start();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"模块装载失败，仅框架指令可用: {ex.Message}");
            }

            var result = composition.Bus
                .ExecuteAsync(commandText, Source)
                .GetAwaiter()
                .GetResult();

            var exitCode = ExitCodeOf(result);
            Print(result, format, identityAssembly, exitCode);
            return exitCode;
        }
        catch (CommandSyntaxException ex)
        {
            WriteError($"命令语法错误: {ex.Message}", format);
            return 2;
        }
        catch (Exception ex)
        {
            WriteError($"命令行执行失败: {ex.GetType().Name}", format, exitCode: 1);
            return 1;
        }
        finally
        {
            composition?.Dispose();
        }
    }

    /// <summary>
    /// 来源标签。与 Web 的 <c>Shell:…</c> 明确区分：留痕里要看得出这条是谁下的。
    /// </summary>
    private const string Source = "cli:local";

    private static void Print(
        CommandResult result,
        HostOutputFormat format,
        Assembly identityAssembly,
        int exitCode)
    {
        if (format == HostOutputFormat.Json)
        {
            var data = result.Data;
            var envelope = new CliResultEnvelope(
                RunId: Guid.NewGuid().ToString("N"),
                Success: result.Success,
                ExitCode: exitCode,
                ExecutionTarget: "offline-composition",
                CandidatePath: ReadString(data, "CandidatePath", "Candidate"),
                InstalledPath: ReadString(data, "InstalledPath", "Installed"),
                RuntimeAck: ReadValue(data, "RuntimeAck", "Ack"),
                LogPath: ReadString(data, "LogPath", "Log"),
                Diagnostics: string.IsNullOrWhiteSpace(result.Message) ? [] : [result.Message],
                Data: data);
            Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
            return;
        }

        Console.WriteLine($"executionTarget=offline-composition processId={Environment.ProcessId} "
            + $"hostVersion={HistoryVulcan.Core.AppIdentity.From(identityAssembly).Version}");
        if (!string.IsNullOrEmpty(result.Message))
            Console.WriteLine(result.Message);

        // 结构化数据按 JSON 打到标准输出，供脚本消费；没有数据时不打空对象。
        if (result.Data == null)
            return;

        Console.WriteLine(JsonSerializer.Serialize(result.Data, JsonOptions));
    }

    private static void WriteError(string message, HostOutputFormat format, int exitCode = 2)
    {
        if (format == HostOutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new CliResultEnvelope(
                Guid.NewGuid().ToString("N"), false, exitCode, "offline-composition",
                null, null, null, null, [message]), JsonOptions));
            return;
        }

        Console.Error.WriteLine(message);
    }

    private static object? ReadValue(object? data, params string[] names)
        => names.Select(name => data?.GetType().GetProperty(name)?.GetValue(data))
            .FirstOrDefault(value => value != null);

    private static string? ReadString(object? data, params string[] names)
        => ReadValue(data, names)?.ToString();

    private static int ExitCodeOf(CommandResult result)
        => result.Success ? 0 : ReadValue(result.Data, "ExitCode") is int exitCode ? exitCode : 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
    };
}
