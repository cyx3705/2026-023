using System.IO.Pipes;
using System.Text.Json;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;

namespace HistoryVulcan.ServiceHost;

internal sealed class RuntimePipeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "portunus.mcp.status",
        "portunus.mcp.start",
        "portunus.mcp.stop",
        "vulcan.module.list",
        "vulcan.module.reload",
        "vulcan.module.install",
    };

    private readonly ServiceComposition _composition;
    private readonly string _pipeName;
    private readonly RuntimeAck _ack;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;

    private RuntimePipeServer(ServiceComposition composition, string identityName, string hostVersion)
    {
        _composition = composition;
        _pipeName = RuntimePipeProtocol.PipeName(identityName);
        _ack = new RuntimeAck(
            Environment.ProcessId,
            DateTimeOffset.UtcNow,
            hostVersion,
            Guid.NewGuid().ToString("N"),
            "",
            []);
        _loop = Task.Run(ServeAsync);
    }

    public static RuntimePipeServer Start(ServiceComposition composition, string identityName, string hostVersion)
        => new(composition, identityName, hostVersion);

    private async Task ServeAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                    4096,
                    4096);
                await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                await HandleAsync(pipe).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                if (!_shutdown.IsCancellationRequested)
                    await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        var challenge = Guid.NewGuid().ToString("N");
        var hello = new RuntimeHello(
            "hello", challenge, _ack.ProcessId, _ack.StartedUtc, _ack.HostVersion, _ack.InstanceId,
            CurrentModuleInstanceIds());
        await writer.WriteLineAsync(JsonSerializer.Serialize(hello, JsonOptions)).ConfigureAwait(false);
        var request = await ReadAsync<RuntimeRequest>(reader).ConfigureAwait(false);
        if (request is null)
            return;

        var name = CommandParser.Parse(request.Command).Name;
        if (!request.Type.Equals(RuntimePipeProtocol.RequestType, StringComparison.OrdinalIgnoreCase)
            || !request.Challenge.Equals(challenge, StringComparison.Ordinal)
            || request.HostProcessId != _ack.ProcessId
            || request.HostStartedUtc != _ack.StartedUtc
            || !request.HostInstanceId.Equals(_ack.InstanceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(name))
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {request.Command}\n运行时握手或请求无效。", null, null)).ConfigureAwait(false);
            return;
        }

        if (!Allowed.Contains(name))
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {request.Command}\n指令 {name} 不在 runtime 白名单中；请使用 GUI 控制台或 --cli 离线恢复。", null,
                Ack(request.RequestId))).ConfigureAwait(false);
            return;
        }

        var action = name.Equals("portunus.mcp.start", StringComparison.OrdinalIgnoreCase)
            || name.Equals("portunus.mcp.stop", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vulcan.module.reload", StringComparison.OrdinalIgnoreCase)
            || name.Equals("vulcan.module.install", StringComparison.OrdinalIgnoreCase);
        if (action && !request.Approve)
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {request.Command}\n指令 {name} 是运行时动作，必须显式提供 --approve。", null,
                Ack(request.RequestId))).ConfigureAwait(false);
            return;
        }

        if (!_composition.Registry.TryGet(name, out _))
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {request.Command}\n运行宿主当前未注册 {name}；请先检查模块状态。", null,
                Ack(request.RequestId))).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _composition.Bus.ExecuteAsync(request.Command, "cli:runtime")
                .ConfigureAwait(false);
            await WriteAsync(writer, new RuntimeResponse(
                result.Success, result.Message, result.Data, Ack(request.RequestId))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {request.Command}\n运行时执行失败：{ex.Message}", null, Ack(request.RequestId))).ConfigureAwait(false);
        }
    }

    private RuntimeAck Ack(string requestId)
        => _ack with
        {
            RequestId = requestId,
            ModuleInstanceIds = CurrentModuleInstanceIds(),
        };

    private IReadOnlyList<string> CurrentModuleInstanceIds()
        => _composition.Modules?.Modules
            .Select(module => module.InstanceId)
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .ToList() ?? [];

    private static async Task<T?> ReadAsync<T>(StreamReader reader)
    {
        var line = await reader.ReadLineAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line)
            ? default
            : JsonSerializer.Deserialize<T>(line, JsonOptions);
    }

    private static Task WriteAsync(StreamWriter writer, RuntimeResponse response)
        => writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _loop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
