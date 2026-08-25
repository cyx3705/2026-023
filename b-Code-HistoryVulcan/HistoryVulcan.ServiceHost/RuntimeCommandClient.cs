using System.IO.Pipes;
using System.Reflection;
using System.Text.Json;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

/// <summary>连接当前用户运行中宿主的受限 CLI 客户端。</summary>
public static class RuntimeCommandClient
{
    /// <summary>运行时管道不可达时的专用退出码。</summary>
    public const int UnreachableExitCode = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>执行一条受限运行时命令；不会回退到离线组合。</summary>
    public static int Run(
        string commandText,
        Assembly identityAssembly,
        HostOutputFormat format = HostOutputFormat.Human,
        bool approve = false)
    {
        var runId = Guid.NewGuid().ToString("N");
        try
        {
            var result = ExecuteAsync(commandText, identityAssembly, approve).GetAwaiter().GetResult();
            var exitCode = result.Success ? 0 : 1;
            Write(result, runId, exitCode, format);
            return exitCode;
        }
        catch (TimeoutException ex)
        {
            WriteFailure(runId, commandText, ex.Message, format, UnreachableExitCode);
            return UnreachableExitCode;
        }
        catch (IOException ex)
        {
            WriteFailure(runId, commandText, $"运行时不可达：{ex.Message}", format, UnreachableExitCode);
            return UnreachableExitCode;
        }
        catch (Exception ex)
        {
            WriteFailure(runId, commandText, $"运行时命令失败：{ex.Message}", format, 1);
            return 1;
        }
    }

    private static async Task<RuntimeResponse> ExecuteAsync(
        string commandText,
        Assembly identityAssembly,
        bool approve)
    {
        var identity = AppIdentity.From(identityAssembly);
        using var pipe = new NamedPipeClientStream(
            ".", RuntimePipeProtocol.PipeName(identity.Name), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(1500).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        var hello = await ReadAsync<RuntimeHello>(reader).ConfigureAwait(false)
            ?? throw new IOException("运行时握手为空。");
        var request = new RuntimeRequest(
            RuntimePipeProtocol.RequestType,
            hello.Challenge,
            Guid.NewGuid().ToString("N"),
            commandText,
            approve,
            hello.ProcessId,
            hello.StartedUtc,
            hello.InstanceId,
            hello.ModuleInstanceIds);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions)).ConfigureAwait(false);
        return await ReadAsync<RuntimeResponse>(reader).ConfigureAwait(false)
            ?? throw new IOException("运行时没有返回结果。");
    }

    private static async Task<T?> ReadAsync<T>(StreamReader reader)
    {
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line)
            ? default
            : JsonSerializer.Deserialize<T>(line, JsonOptions);
    }

    private static void Write(RuntimeResponse result, string runId, int exitCode, HostOutputFormat format)
    {
        if (format == HostOutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new CliResultEnvelope(
                runId, result.Success, exitCode, "runtime-host", null, null,
                result.RuntimeAck, null, result.Success ? [] : [result.Message]), JsonOptions));
            return;
        }

        Console.WriteLine($"executionTarget=runtime-host processId={result.RuntimeAck?.ProcessId ?? 0} "
            + $"hostVersion={result.RuntimeAck?.HostVersion ?? "unknown"}");
        Console.WriteLine(result.Message);
        if (result.Data is not null)
            Console.WriteLine(JsonSerializer.Serialize(result.Data, JsonOptions));
    }

    private static void WriteFailure(string runId, string commandText, string message, HostOutputFormat format, int exitCode)
    {
        message = $"原始命令: {commandText}\n{message}";
        if (format == HostOutputFormat.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new CliResultEnvelope(
                runId, false, exitCode, "runtime-host", null, null, null, null, [message]), JsonOptions));
        }
        else
        {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine("请确认 HistoryVulcan 后台正在运行；runtime 不会降级为 offline。");
        }
    }
}

internal static class RuntimePipeProtocol
{
    public const string RequestType = "request";
    public static string PipeName(string appName)
        => $"HistoryVulcan.{Sanitize(appName)}.runtime";

    private static string Sanitize(string value)
        => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
}

internal sealed record RuntimeHello(
    string Type,
    string Challenge,
    int ProcessId,
    DateTimeOffset StartedUtc,
    string HostVersion,
    string InstanceId,
    IReadOnlyList<string> ModuleInstanceIds);

internal sealed record RuntimeRequest(
    string Type,
    string Challenge,
    string RequestId,
    string Command,
    bool Approve,
    int HostProcessId,
    DateTimeOffset HostStartedUtc,
    string HostInstanceId,
    IReadOnlyList<string> ModuleInstanceIds);

internal sealed record RuntimeAck(
    int ProcessId,
    DateTimeOffset StartedUtc,
    string HostVersion,
    string InstanceId,
    string RequestId,
    IReadOnlyList<string> ModuleInstanceIds);

internal sealed record RuntimeResponse(
    bool Success,
    string Message,
    object? Data,
    RuntimeAck? RuntimeAck);
