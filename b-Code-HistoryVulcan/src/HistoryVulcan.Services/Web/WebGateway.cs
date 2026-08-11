using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Web;

/// <summary>命令总线的 HTTP/WS 接入点，供本机 Shell 与受鉴权 Web 客户端共用。</summary>
public sealed partial class WebGateway : IDisposable
{
    private const string LanProtocolVersion = "3.0.0";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyPort = "web.port";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyBind = "web.bind";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyToken = "web.token";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyCors = "web.cors";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyConfirm = "web.confirm";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyRateLimit = "web.ratelimit";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyRateWindowLimit = "web.ratewindowlimit";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyPortRetries = "web.portretries";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyFrontendCatalog = "web.frontendcatalog";
    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public const string KeyFrontendCatalogLimit = "web.frontendcataloglimit";

    private const int DefaultPortBase = 8938;
    private const int DefaultPortSpan = 200;
    private const int DefaultPortRetries = 20;
    private const int DefaultRateWindowLimit = 4096;
    private const int DefaultFrontendCatalogLimit = 32;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
    };

    private readonly Func<CommandBus?> _busAccessor;
    private readonly ISettingsService _settings;
    private readonly IShellLog _log;
    private readonly object _lifecycleLock = new();
    private readonly ConcurrentDictionary<string, EventClient> _clients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommand> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingConfirmation> _pendingConfirmations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FrontendCapabilityCatalog> _sessionCatalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrontendCapabilityCatalog> _cachedCatalogs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catalogLock = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public WebGateway(Func<CommandBus?> busAccessor, ISettingsService settings, IShellLog log)
    {
        _busAccessor = busAccessor;
        _settings = settings;
        _log = log;
        _log.EntryAdded += OnLogEntry;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IDeviceAuthenticationProvider? DeviceAuthentication { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public IDevicePairingProvider? DevicePairing { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string ServerId { get; set; } = AppIdentity.Current.Name;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int Port { get; private set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string BindAddress => NormalizeBind(_settings.Get(KeyBind));

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public string ActiveBindAddress { get; private set; } = "";

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int ConnectedClients => _clients.Count;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int ConnectedShells => _clients.Values.Count(client => client.Session.Kind == ClientKind.Shell);

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void PublishModuleRevision(long revision)
    {
        var payload = new JsonObject
        {
            ["type"] = "moduleRevision",
            ["revision"] = revision,
        };
        foreach (var client in _clients.Values.Where(client => client.Session.Kind == ClientKind.Shell))
            client.TryQueueLog(payload);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public int DisconnectDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return 0;
        var disconnected = 0;
        foreach (var item in _clients.Where(item =>
                     item.Value.Session.DeviceId?.Equals(
                         deviceId, StringComparison.Ordinal) == true).ToList())
        {
            if (!_clients.TryRemove(item.Key, out var client))
                continue;
            _sessionCatalogs.TryRemove(client.Session.Id, out _);
            _rateWindows.TryRemove(client.Session.Id, out _);
            CompletePendingForSession(client.Session.Id);
            client.Dispose();
            disconnected++;
        }
        return disconnected;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool TryGetSession(string source, out ClientSession session)
    {
        var id = SessionIdFromSource(source);
        if (id != null && _clients.TryGetValue(id, out var client))
        {
            session = client.Session;
            return true;
        }
        session = null!;
        return false;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public (bool Success, string Message) Start(int? port = null)
    {
        lock (_lifecycleLock)
        {
            if (IsRunning)
                return (false, $"Web 服务已在运行(端口 {Port})");

            RestoreCachedFrontendCatalogs();

            var configured = int.TryParse(
                _settings.Get(KeyPort), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var configuredPort)
                ? configuredPort
                : (int?)null;
            var initialPort = port ?? configured ?? DeriveDefaultPort(ServerId);
            if (initialPort is < 1024 or > 65535)
                return (false, $"端口无效: {initialPort}(允许 1024~65535)");

            var bind = BindAddress;
            var token = _settings.Get(KeyToken);
            if (!IsLoopback(bind) && string.IsNullOrWhiteSpace(token)
                                  && DeviceAuthentication == null)
                return (false, "非 localhost 绑定必须配置设备鉴权或非空 web.token");

            var retries = Math.Clamp(
                _settings.GetInt(KeyPortRetries, DefaultPortRetries), 0, 100);
            Exception? lastError = null;
            for (var attempt = 0; attempt <= retries; attempt++)
            {
                var candidate = initialPort + attempt;
                if (candidate > 65535)
                    break;
                try
                {
                    var listener = new HttpListener();
                    var scheme = IsLoopback(bind) ? "http" : "https";
                    listener.Prefixes.Add($"{scheme}://{bind}:{candidate}/");
                    if (!IsLoopback(bind))
                        listener.Prefixes.Add($"http://127.0.0.1:{candidate}/");
                    listener.Start();
                    _listener = listener;
                    Port = candidate;
                    ActiveBindAddress = bind;
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _listener?.Close();
                    _listener = null;
                }
            }

            if (_listener == null)
                return (false,
                    $"监听失败: 从端口 {initialPort} 起连续尝试 {retries + 1} 个端口均不可用: {lastError?.Message}");

            if (port.HasValue || configured.HasValue)
                _settings.Set(KeyPort, Port.ToString(System.Globalization.CultureInfo.InvariantCulture));

            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
            var publicScheme = IsLoopback(bind) ? "http" : "https";
            _log.Info("web", $"Web 服务已启动: {publicScheme}://{bind}:{Port}/");
            return (true, $"Web 服务已启动: {publicScheme}://{bind}:{Port}/");
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public (bool Success, string Message) Stop()
    {
        lock (_lifecycleLock)
        {
            if (!IsRunning)
                return (false, "Web 服务未在运行");

            var releasedPort = Port;
            _cts?.Cancel();
            _listener?.Close();
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            ActiveBindAddress = "";
            foreach (var client in _clients.Values)
                client.Dispose();
            _clients.Clear();
            foreach (var pending in _pending.Values)
                pending.Completion.TrySetResult(CommandResult.Fail("前端连接已断开"));
            _pending.Clear();
            foreach (var confirmation in _pendingConfirmations.Values)
                confirmation.Completion.TrySetResult(false);
            _pendingConfirmations.Clear();
            _sessionCatalogs.Clear();
            _rateWindows.Clear();
            Port = 0;
            _log.Info("web", "Web 服务已停止");
            return (true, $"Web 服务已停止(端口 {releasedPort} 已释放)");
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task<CommandResult> RelayFrontendCommandAsync(
        string text,
        string source,
        CancellationToken cancellationToken)
    {
        ParsedCommand parsed;
        try
        {
            parsed = CommandParser.Parse(text);
        }
        catch (CommandSyntaxException ex)
        {
            return CommandResult.Fail($"前端命令语法错误: {ex.Message}");
        }

        var target = parsed.Named.GetValueOrDefault("_frontend");
        var candidates = _clients.Values
            .Where(client => client.Session.Kind == ClientKind.Shell
                             && client.Socket.State == WebSocketState.Open)
            .Where(client => !_sessionCatalogs.TryGetValue(client.Session.Id, out var catalog)
                             || catalog.Commands.Any(command => command.Name.Equals(
                                 parsed.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        EventClient? frontend = null;
        if (!string.IsNullOrWhiteSpace(target))
        {
            var matched = candidates.Where(client =>
                    client.Session.Id.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || client.Session.Name.Equals(target, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matched.Count != 1)
                return CommandResult.Fail(matched.Count == 0
                    ? $"目标前端不可用: {target}"
                    : $"目标前端不唯一: {target}，请改用会话 ID");
            frontend = matched[0];
        }
        else
        {
            var originSessionId = SessionIdFromSource(source);
            frontend = originSessionId == null
                ? null
                : candidates.FirstOrDefault(client => client.Session.Id.Equals(
                    originSessionId, StringComparison.Ordinal));
            if (frontend == null && candidates.Count == 1)
                frontend = candidates[0];
            if (frontend == null && candidates.Count > 1)
                return CommandResult.Fail("多个前端可执行该命令，请指定 _frontend=<会话 ID 或应用名>");
        }

        if (frontend == null)
            return CommandResult.Fail("前端不可用");
        var relayText = RemoveFrontendTarget(parsed);

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingCommand(frontend.Session.Id, completion)))
            return CommandResult.Fail("无法创建前端命令关联 ID");

        try
        {
            await SendAsync(frontend, new JsonObject
            {
                ["type"] = "uiCommand",
                ["id"] = id,
                ["text"] = relayText,
                ["source"] = source,
            }, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CommandResult.Fail(cancellationToken.IsCancellationRequested
                ? "前端命令已取消"
                : "前端命令响应超时");
        }
        catch (WebSocketException)
        {
            return CommandResult.Fail("前端连接已断开");
        }
        catch (ObjectDisposedException)
        {
            return CommandResult.Fail("前端连接已断开");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public bool RequestWebConfirmation(string prompt, string source, TimeSpan timeout)
    {
        var sessionId = SessionIdFromSource(source);
        if (sessionId == null || !_clients.TryGetValue(sessionId, out var client)
            || client.Socket.State != WebSocketState.Open)
            return false;

        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingConfirmations.TryAdd(id, new PendingConfirmation(sessionId, completion)))
            return false;
        try
        {
            var payload = new JsonObject
            {
                ["type"] = "confirmation",
                ["id"] = id,
                ["prompt"] = prompt,
                ["source"] = source,
            };
            _ = SendIgnoringErrorsAsync(client, payload);
            return completion.Task.Wait(timeout) && completion.Task.Result;
        }
        finally
        {
            _pendingConfirmations.TryRemove(id, out _);
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Dispose()
    {
        _log.EntryAdded -= OnLogEntry;
        if (IsRunning)
            Stop();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            ApplyCors(context);
            if (context.Request.HttpMethod == "OPTIONS")
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }

            var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var remoteRateKey = "remote:" + (context.Request.RemoteEndPoint?.Address.ToString() ?? "unknown");
            if (path.Equals("/api/pair", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "POST")
            {
                if (!AllowRequest(remoteRateKey))
                {
                    await WriteRateLimitAsync(context, remoteRateKey).ConfigureAwait(false);
                    return;
                }
                await HandlePairingAsync(context).ConfigureAwait(false);
                return;
            }

            if (IsRateLimitReached(remoteRateKey))
            {
                await WriteRateLimitAsync(context, remoteRateKey).ConfigureAwait(false);
                return;
            }
            var requestedSession = CreateSession(context.Request);
            var session = Authenticate(context.Request, requestedSession);
            if (session == null)
            {
                if (!AllowRequest(remoteRateKey))
                {
                    await WriteRateLimitAsync(context, remoteRateKey).ConfigureAwait(false);
                    return;
                }
                await WriteJsonAsync(context, new { error = "unauthorized" }, 401).ConfigureAwait(false);
                return;
            }

            // The authenticated loopback Shell is the standalone host's control plane. Its
            // catalog refreshes and user commands must not consume the public Web quota.
            // Remote Shell/Web sessions and failed authentication remain rate limited.
            if (!IsTrustedLoopbackShell(session) && !AllowRequest(session.Id))
            {
                await WriteRateLimitAsync(context, session.Id).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/events", StringComparison.OrdinalIgnoreCase)
                && context.Request.IsWebSocketRequest)
            {
                await HandleWebSocketAsync(context, session, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/health", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "GET")
            {
                await WriteJsonAsync(context, new
                {
                    status = "ok",
                    port = Port,
                    bind = BindAddress,
                    clients = ConnectedClients,
                    shells = ConnectedShells,
                    serverId = ServerId,
                    productVersion = AppIdentity.Current.Version,
                    historyVulcanProtocolVersion = LanProtocolVersion,
                    minClientVersion = LanProtocolVersion,
                    capabilities = new[] { "device-auth", "session-affine-ui", "single-exe" },
                }, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/commands", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "GET")
            {
                var bus = _busAccessor();
                if (bus == null)
                {
                    await WriteJsonAsync(context, new { error = "command bus not ready" }, 503).ConfigureAwait(false);
                    return;
                }

                var schemas = new CommandSchemaExporter(bus.Registry).ExportTools()
                    .ToDictionary(tool => tool.CommandName, StringComparer.OrdinalIgnoreCase);
                var commands = bus.Registry.All().Select(descriptor => new
                {
                    descriptor.Name,
                    descriptor.Summary,
                    descriptor.Example,
                    source = bus.Registry.GetSource(descriptor.Name),
                    descriptor.Readonly,
                    dangerous = descriptor.IsDangerous,
                    executionSite = descriptor.ExecutionSite.ToString(),
                    mcpState = McpExposurePolicy.State(descriptor),
                    inputSchema = schemas.GetValueOrDefault(descriptor.Name)?.InputSchema,
                });
                await WriteJsonAsync(context, new { commands }, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/command", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "POST")
            {
                var request = await ReadJsonAsync<CommandRequest>(context.Request).ConfigureAwait(false);
                if (request == null || string.IsNullOrWhiteSpace(request.Text))
                {
                    await WriteJsonAsync(context, new { error = "text is required" }, 400).ConfigureAwait(false);
                    return;
                }

                var bus = _busAccessor();
                if (bus == null)
                {
                    await WriteJsonAsync(context, new { error = "command bus not ready" }, 503).ConfigureAwait(false);
                    return;
                }

                if (!CanExecute(session, bus.Registry, request.Text, out var denial))
                {
                    await WriteJsonAsync(context, CommandResult.Fail(denial), 403).ConfigureAwait(false);
                    return;
                }

                var result = await bus.ExecuteAsync(
                    request.Text,
                    SessionSource(session),
                    cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(context, result, 200).ConfigureAwait(false);
                return;
            }

            if (path.Equals("/api/confirm", StringComparison.OrdinalIgnoreCase)
                && context.Request.HttpMethod == "POST")
            {
                if (session.Kind != ClientKind.Web)
                {
                    await WriteJsonAsync(context, new { error = "web session required" }, 403).ConfigureAwait(false);
                    return;
                }
                var answer = await ReadJsonAsync<ConfirmRequest>(context.Request).ConfigureAwait(false);
                if (answer == null || string.IsNullOrWhiteSpace(answer.Id)
                    || !_pendingConfirmations.TryGetValue(answer.Id, out var pending)
                    || !pending.SessionId.Equals(session.Id, StringComparison.Ordinal))
                {
                    await WriteJsonAsync(context, new { error = "confirmation not found" }, 404).ConfigureAwait(false);
                    return;
                }
                pending.Completion.TrySetResult(answer.Approved);
                await WriteJsonAsync(context, new { accepted = true }, 200).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(context, new { error = "not found" }, 404).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        {
            _log.Warn("web", $"请求体被拒绝: {ex.Message}");
            TryClose(context, 413);
        }
        catch (JsonException ex)
        {
            _log.Warn("web", $"请求 JSON 无效: {ex.Message}");
            TryClose(context, 400);
        }
        catch (Exception ex)
        {
            _log.Error("web", $"请求处理失败: {ex.Message}");
            TryClose(context, 500);
        }
    }

    private async Task HandleWebSocketAsync(
        HttpListenerContext context,
        ClientSession session,
        CancellationToken cancellationToken)
    {
        var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var client = new EventClient(session, accepted.WebSocket);
        if (!_clients.TryAdd(session.Id, client))
        {
            try
            {
                await SendAsync(client, new JsonObject
                {
                    ["type"] = "error",
                    ["error"] = "session_id_in_use",
                    ["message"] = "该 session id 已有活动连接",
                }, CancellationToken.None).ConfigureAwait(false);
                await client.Socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "session id already connected",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                client.Dispose();
            }
            return;
        }
        await SendAsync(client, new JsonObject
        {
            ["type"] = "connected",
            ["sessionId"] = session.Id,
            ["kind"] = session.Kind.ToString(),
        }, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[64 * 1024];
        try
        {
            while (client.Socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var payloadBytes = await WebSocketMessageReader.ReceiveTextAsync(
                    client.Socket, buffer, cancellationToken).ConfigureAwait(false);
                if (payloadBytes == null)
                    break;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(payloadBytes);
                }
                catch (JsonException ex)
                {
                    _log.Warn("web", $"已忽略无效 WebSocket JSON: {ex.Message}");
                    continue;
                }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var type)
                        && type.GetString() == "commandResult"
                        && root.TryGetProperty("id", out var id)
                        && _pending.TryGetValue(id.GetString() ?? "", out var pending)
                        && pending.SessionId.Equals(session.Id, StringComparison.Ordinal))
                    {
                        var success = root.TryGetProperty("success", out var ok) && ok.GetBoolean();
                        var message = root.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                        object? data = root.TryGetProperty("data", out var payload)
                            ? JsonSerializer.Deserialize<object>(payload.GetRawText(), JsonOptions)
                            : null;
                        pending.Completion.TrySetResult(success
                            ? CommandResult.Ok(message, data)
                            : CommandResult.Fail(message));
                    }
                    else if (root.TryGetProperty("type", out type)
                             && type.GetString() == "confirmationResult"
                             && root.TryGetProperty("id", out var confirmationId)
                             && _pendingConfirmations.TryGetValue(
                                 confirmationId.GetString() ?? "", out var confirmation)
                             && confirmation.SessionId.Equals(session.Id, StringComparison.Ordinal))
                    {
                        confirmation.Completion.TrySetResult(
                            root.TryGetProperty("approved", out var approved) && approved.GetBoolean());
                    }
                    else if (root.TryGetProperty("type", out type)
                             && type.GetString() == "commandCatalog"
                             && session.Kind == ClientKind.Shell
                             && root.TryGetProperty("catalog", out var catalogJson))
                    {
                        var catalog = JsonSerializer.Deserialize<FrontendCapabilityCatalog>(
                            catalogJson.GetRawText(), JsonOptions);
                        if (catalog != null)
                            ApplyFrontendCatalog(session, catalog);
                    }
                }
            }
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            var removed = ((ICollection<KeyValuePair<string, EventClient>>)_clients).Remove(
                new KeyValuePair<string, EventClient>(session.Id, client));
            if (removed)
            {
                _sessionCatalogs.TryRemove(session.Id, out _);
                _rateWindows.TryRemove(session.Id, out _);
                CompletePendingForSession(session.Id);
            }
            client.Dispose();
        }
    }

    private void CompletePendingForSession(string sessionId)
    {
        foreach (var item in _pending.Where(item =>
                     item.Value.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToList())
        {
            if (_pending.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult(CommandResult.Fail("发起前端已断开"));
        }
        foreach (var item in _pendingConfirmations.Where(item =>
                     item.Value.SessionId.Equals(sessionId, StringComparison.Ordinal)).ToList())
        {
            if (_pendingConfirmations.TryRemove(item.Key, out var pending))
                pending.Completion.TrySetResult(false);
        }
    }

    private void ApplyFrontendCatalog(ClientSession session, FrontendCapabilityCatalog catalog)
    {
        var registry = _busAccessor()?.Registry;
        if (registry == null || string.IsNullOrWhiteSpace(catalog.FrontendName)
            || catalog.Commands.Count > 4096)
            return;
        var commands = catalog.Commands
            .Where(command => !string.IsNullOrWhiteSpace(command.Name))
            .GroupBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var normalized = catalog with { Commands = commands };

        lock (_catalogLock)
        {
            _sessionCatalogs[session.Id] = normalized;
            _cachedCatalogs[normalized.FrontendName] = normalized;
            TrimCatalogCache(normalized.FrontendName);
            ApplyCatalogToRegistry(registry, normalized);
            _settings.Set(KeyFrontendCatalog, JsonSerializer.Serialize(
                _cachedCatalogs.Values.OrderBy(item => item.FrontendName).ToList(), JsonOptions));
        }
        _log.Info("web", $"已同步前端命令目录: {normalized.FrontendName}，{commands.Count} 条");
    }

    private void RestoreCachedFrontendCatalogs()
    {
        var registry = _busAccessor()?.Registry;
        var json = _settings.Get(KeyFrontendCatalog);
        if (registry == null || string.IsNullOrWhiteSpace(json))
            return;
        lock (_catalogLock)
        {
            if (_cachedCatalogs.Count > 0)
                return;
            try
            {
                var catalogs = JsonSerializer.Deserialize<List<FrontendCapabilityCatalog>>(json, JsonOptions) ?? [];
                foreach (var catalog in catalogs.Where(item => item.Commands.Count <= 4096))
                {
                    _cachedCatalogs[catalog.FrontendName] = catalog;
                    TrimCatalogCache(catalog.FrontendName);
                    ApplyCatalogToRegistry(registry, catalog);
                }
            }
            catch (JsonException ex)
            {
                _log.Warn("web", $"缓存的前端命令目录无效，已忽略: {ex.Message}");
            }
        }
    }

    private void TrimCatalogCache(string keepName)
    {
        var limit = Math.Clamp(
            _settings.GetInt(KeyFrontendCatalogLimit, DefaultFrontendCatalogLimit), 1, 256);
        foreach (var name in _cachedCatalogs.Keys
                     .Where(name => !name.Equals(keepName, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                     .Take(Math.Max(0, _cachedCatalogs.Count - limit))
                     .ToList())
        {
            _cachedCatalogs.Remove(name);
        }
    }

    private static void ApplyCatalogToRegistry(CommandRegistry registry, FrontendCapabilityCatalog catalog)
    {
        foreach (var command in catalog.Commands)
        {
            try
            {
                if (!CommandParser.Parse(command.Name).Name.Equals(command.Name, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            catch (CommandSyntaxException)
            {
                continue;
            }

            if (registry.TryGet(command.Name, out var existing))
            {
                if (existing.ExecutionSite != CommandExecutionSite.Frontend)
                    continue;
                registry.Unregister(command.Name);
            }
            registry.Register(command.CreateProxy(), $"frontend:{catalog.FrontendName}");
        }
    }

    private static string RemoveFrontendTarget(ParsedCommand parsed)
    {
        var parts = new List<string> { parsed.Name };
        parts.AddRange(parsed.Positionals.Select(CommandParser.QuoteArg));
        parts.AddRange(parsed.Named
            .Where(pair => !pair.Key.Equals("_frontend", StringComparison.OrdinalIgnoreCase))
            .Select(pair => $"{pair.Key}={CommandParser.QuoteArg(pair.Value)}"));
        return string.Join(' ', parts);
    }

    private ClientSession CreateSession(HttpListenerRequest request)
    {
        var kind = request.Headers["X-HistoryVulcan-Client"]?.Equals("Shell", StringComparison.OrdinalIgnoreCase) == true
            ? ClientKind.Shell
            : ClientKind.Web;
        var name = request.Headers["X-Client-Name"];
        var id = request.Headers["X-Session-Id"];
        id ??= StableSessionId(kind, request.RemoteEndPoint?.Address, name);
        var address = request.RemoteEndPoint?.Address;
        return ClientSession.Create(
            kind,
            name ?? kind.ToString(),
            id: id,
            remoteAddress: address?.ToString(),
            deviceId: request.Headers["X-Device-Id"],
            isLoopback: address != null && IPAddress.IsLoopback(address));
    }

    private ClientSession? Authenticate(HttpListenerRequest request, ClientSession requested)
    {
        if (!requested.IsLoopback && !request.IsSecureConnection)
            return null;

        if (requested.Kind == ClientKind.Shell && requested.IsLoopback)
        {
            return requested with
            {
                AuthSubject = "loopback-shell",
                Scopes = new HashSet<string>(
                    ["read", "operate", "admin"], StringComparer.OrdinalIgnoreCase),
            };
        }

        var bearer = ReadBearer(request);
        var deviceId = request.Headers["X-Device-Id"];
        if (!string.IsNullOrWhiteSpace(bearer)
            && !string.IsNullOrWhiteSpace(deviceId)
            && DeviceAuthentication != null)
        {
            var authenticated = DeviceAuthentication.Authenticate(
                deviceId, bearer, requested.RemoteAddress);
            if (authenticated.Success)
            {
                return requested with
                {
                    DeviceId = authenticated.DeviceId,
                    AuthSubject = authenticated.Subject,
                    Scopes = authenticated.Scopes,
                };
            }
        }

        var token = _settings.Get(KeyToken);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(bearer)
            || !FixedEquals(token, bearer))
            return null;

        return requested with
        {
            Kind = ClientKind.Web,
            AuthSubject = "legacy-web-token",
            Scopes = new HashSet<string>(["read", "operate"], StringComparer.OrdinalIgnoreCase),
        };
    }

}
