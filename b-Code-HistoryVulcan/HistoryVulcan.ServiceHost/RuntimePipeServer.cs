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
        // 只读的就绪查询，与 module.list 同一档。5.1.3 加了这条命令时两份名单都没改，
        // 5.2 先补了 CliExposurePolicy（--cli）才发现 --runtime 另有这一份——
        // 同一件事写在两处，改一处漏一处只是时间问题。新增查询类指令时两份都要过一遍。
        "vulcan.module.ready",
        "vulcan.module.reload",
        "vulcan.module.install",
    };

    private readonly ServiceComposition _composition;
    private readonly string _pipeName;
    private readonly RuntimeAck _ack;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _loop;
    private NamedPipeServerStream? _activePipe;
    private int _disposed;

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
            [],
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
                Interlocked.Exchange(ref _activePipe, pipe);
                try
                {
                    await pipe.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                    await HandleAsync(pipe).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.CompareExchange(ref _activePipe, null, pipe);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException
                                       or JsonException or InvalidDataException)
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
        RuntimeRequest? request;
        try
        {
            request = await ReadAsync<RuntimeRequest>(reader, _shutdown.Token).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAsync(writer, new RuntimeResponse(
                false, $"运行时请求 JSON 无效：{ex.Message}", null, null)).ConfigureAwait(false);
            return;
        }
        if (request is null)
            return;

        var command = request.Command ?? "";
        string name;
        try
        {
            name = CommandParser.Parse(command).Name;
        }
        catch (CommandSyntaxException ex)
        {
            await WriteAsync(writer, new RuntimeResponse(
                false, $"原始命令: {command}\n运行时指令无效：{ex.Message}", null, null)).ConfigureAwait(false);
            return;
        }
        if (!string.Equals(request.Type, RuntimePipeProtocol.RequestType, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(request.Challenge, challenge, StringComparison.Ordinal)
            || request.HostProcessId != _ack.ProcessId
            || request.HostStartedUtc != _ack.StartedUtc
            || !string.Equals(request.HostInstanceId, _ack.InstanceId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(name))
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {command}\n运行时握手或请求无效。", null, null)).ConfigureAwait(false);
            return;
        }

        if (!Allowed.Contains(name))
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {command}\n指令 {name} 不在 runtime 白名单中；请使用 GUI 控制台或 --cli 离线恢复。", null,
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
                $"原始命令: {command}\n指令 {name} 是运行时动作，必须显式提供 --approve。", null,
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
            var result = await _composition.Bus.ExecuteAsync(command, "cli:runtime")
                .ConfigureAwait(false);
            await WriteAsync(writer, new RuntimeResponse(
                result.Success, result.Message, result.Data, Ack(request.RequestId))).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAsync(writer, new RuntimeResponse(false,
                $"原始命令: {command}\n运行时执行失败：{ex.Message}", null, Ack(request.RequestId))).ConfigureAwait(false);
        }
    }

    private RuntimeAck Ack(string requestId)
        => _ack with
        {
            RequestId = requestId,
            ModuleInstanceIds = CurrentModuleInstanceIds(),
            Modules = CurrentModules(),
        };

    private IReadOnlyList<string> CurrentModuleInstanceIds()
        => CurrentModules()
            .Select(module => module.InstanceId)
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId))
            .ToList();

    private IReadOnlyList<RuntimeModuleAck> CurrentModules()
        => _composition.Modules?.Modules
            .Select(module => new RuntimeModuleAck(
                module.ModuleName,
                module.Version,
                module.CommandCount,
                module.InstanceId,
                module.Attached))
            .ToList() ?? [];

    private static async Task<T?> ReadAsync<T>(StreamReader reader, CancellationToken cancellation)
    {
        var line = await reader.ReadLineAsync(cancellation).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(line)
            ? default
            : JsonSerializer.Deserialize<T>(line, JsonOptions);
    }

    private static Task WriteAsync(StreamWriter writer, RuntimeResponse response)
        => writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        Interlocked.Exchange(ref _activePipe, null)?.Dispose();
        try { _loop.GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (JsonException) { }
        _shutdown.Dispose();
    }
}
