using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace HistoryVulcan.Services.Web;

/// <summary>WPF Shell 使用的命令 API 与 UI 命令中继客户端。</summary>
public sealed class ShellServiceClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly HttpClient _http;
    private readonly HttpClientHandler _httpHandler;
    private readonly Uri _baseUri;
    private readonly ShellEndpointProfile _profile;
    private readonly string _name;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _lifetime = new();
    private ClientWebSocket? _events;
    private int _disposed;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public Func<string, JsonElement, object?>? DataDeserializer { get; set; }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellConnectionState State { get; private set; } = ShellConnectionState.Disconnected;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public event Action<ShellConnectionState>? StateChanged;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public event EventHandler<ShellLogEntry>? LogReceived;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public event Action<long>? ModuleRevisionReceived;

    internal TimeSpan RemoteRequestTimeout => _profile.EffectiveConnectTimeout;

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellServiceClient(Uri baseUri, string name)
        : this(new ShellEndpointProfile(baseUri, Guid.NewGuid().ToString("N")), name)
    {
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public ShellServiceClient(ShellEndpointProfile profile, string name)
    {
        _profile = profile;
        _baseUri = profile.BaseUri;
        _name = name;
        _httpHandler = CreateHttpHandler(profile);
        _http = new HttpClient(_httpHandler, disposeHandler: false)
        {
            BaseAddress = _baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("X-HistoryVulcan-Client", "Shell");
        _http.DefaultRequestHeaders.Add("X-Client-Name", name);
        _http.DefaultRequestHeaders.Add("X-Session-Id", _sessionId);
        _http.DefaultRequestHeaders.Add("X-Device-Id", profile.DeviceId);
    }

    private static HttpClientHandler CreateHttpHandler(ShellEndpointProfile profile)
    {
        var handler = new HttpClientHandler();
        if (!profile.BaseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return handler;

        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            var hasPin = ShellEndpointProfile.NormalizeFingerprint(
                profile.CertificateFingerprint).Length > 0;
            return hasPin
                ? profile.ValidateCertificate(certificate == null ? null : new(certificate))
                : errors == System.Net.Security.SslPolicyErrors.None && profile.BaseUri.IsLoopback;
        };
        return handler;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task<bool> WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        SetState(ShellConnectionState.Connecting);
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "api/health");
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    var health = await response.Content.ReadFromJsonAsync<HealthEnvelope>(
                        JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (health == null)
                        continue;
                    if (!string.IsNullOrWhiteSpace(_profile.ServerId)
                        && !_profile.ServerId.Equals(health.ServerId, StringComparison.Ordinal))
                    {
                        SetState(ShellConnectionState.Rejected);
                        return false;
                    }
                    var clientVersion = typeof(ShellServiceClient).Assembly.GetName().Version
                                        ?? new Version(0, 0);
                    if (Version.TryParse(health.MinClientVersion, out var minimum)
                        && minimum > clientVersion)
                    {
                        SetState(ShellConnectionState.VersionMismatch);
                        return false;
                    }
                    SetState(ShellConnectionState.Ready);
                    return true;
                }
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    SetState(ShellConnectionState.PairingRequired);
            }
            catch (HttpRequestException)
            {
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        if (State == ShellConnectionState.Connecting)
            SetState(ShellConnectionState.Disconnected);
        return false;
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task<CommandResult> ExecuteAsync(
        string text,
        string source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "api/command");
            request.Content = JsonContent.Create(new { text, source }, options: JsonOptions);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return await DescribeCommandFailureAsync(response, cancellationToken).ConfigureAwait(false);

            var result = await response.Content.ReadFromJsonAsync<ResultEnvelope>(
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (result == null)
                return CommandResult.Fail("服务返回了空结果");
            object? data = null;
            if (result.Data is { } element)
            {
                var commandName = CommandName(text);
                try
                {
                    data = DataDeserializer?.Invoke(commandName, element) ?? element.Clone();
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return CommandResult.Fail(
                        $"服务结果无法还原: command={commandName}, kind={element.ValueKind}: {ex.Message}");
                }
            }
            return new CommandResult
            {
                Success = result.Success,
                Message = result.Message ?? "",
                Data = data,
            };
        }
        catch (HttpRequestException ex)
        {
            return CommandResult.Fail($"后台服务未连接: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Fail("后台服务请求超时");
        }
    }

    private static async Task<CommandResult> DescribeCommandFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.TooManyRequests)
            return CommandResult.Fail($"服务命令请求失败: HTTP {(int)response.StatusCode}");

        int? retryAfterSeconds = response.Headers.RetryAfter?.Delta is { } delta
            ? Math.Max(1, (int)Math.Ceiling(delta.TotalSeconds))
            : null;
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RateLimitEnvelope>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            retryAfterSeconds ??= error?.RetryAfterSeconds;
        }
        catch (JsonException)
        {
        }

        return CommandResult.Fail(retryAfterSeconds is > 0
            ? $"服务请求触发限流: HTTP 429，请在 {retryAfterSeconds} 秒后重试"
            : "服务请求触发限流: HTTP 429，请稍后重试");
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task<DevicePairingResult> PairAsync(
        string code,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/pair")
            {
                Content = JsonContent.Create(new
                {
                    code,
                    deviceId = _profile.DeviceId,
                    deviceName,
                }, options: JsonOptions),
            };
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<DevicePairingResult>(
                JsonOptions, cancellationToken).ConfigureAwait(false);
            return result ?? new DevicePairingResult(
                false, null, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "empty pairing response");
        }
        catch (HttpRequestException)
        {
            return new DevicePairingResult(
                false, null, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "pairing connection failed");
        }
        catch (JsonException)
        {
            return new DevicePairingResult(
                false, null, null, null,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                "invalid pairing response");
        }
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        try { _events?.Abort(); } catch (WebSocketException) { }
        SetState(ShellConnectionState.Disconnected);
        return await WaitForReadyAsync(_profile.EffectiveConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public async Task RunEventLoopAsync(
        CommandBus localBus,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token, cancellationToken);
        await RunEventLoopCoreAsync(localBus, linked.Token).ConfigureAwait(false);
    }

    private async Task RunEventLoopCoreAsync(CommandBus localBus, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _events?.Dispose();
                _events = new ClientWebSocket();
                _events.Options.SetRequestHeader("X-HistoryVulcan-Client", "Shell");
                _events.Options.SetRequestHeader("X-Client-Name", _name);
                _events.Options.SetRequestHeader("X-Session-Id", _sessionId);
                _events.Options.SetRequestHeader("X-Device-Id", _profile.DeviceId);
                var token = _profile.AccessTokenProvider?.Invoke();
                if (!string.IsNullOrWhiteSpace(token))
                    _events.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                if (_baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    _events.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    {
                        var hasPin = ShellEndpointProfile.NormalizeFingerprint(
                            _profile.CertificateFingerprint).Length > 0;
                        return hasPin
                            ? _profile.ValidateCertificate(certificate == null ? null : new(certificate))
                            : errors == System.Net.Security.SslPolicyErrors.None && _profile.BaseUri.IsLoopback;
                    };
                }
                var builder = new UriBuilder(_baseUri)
                {
                    Scheme = _baseUri.Scheme == "https" ? "wss" : "ws",
                    Path = "/api/events",
                };
                await _events.ConnectAsync(builder.Uri, cancellationToken).ConfigureAwait(false);
                await PublishCommandCatalogAsync(_events, localBus.Registry, cancellationToken)
                    .ConfigureAwait(false);
                await ReceiveEventsAsync(_events, localBus, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (WebSocketException)
            {
            }
            catch (HttpRequestException)
            {
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"HistoryVulcan 前端事件循环异常，将重连: {ex}");
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PublishCommandCatalogAsync(
        ClientWebSocket socket,
        CommandRegistry registry,
        CancellationToken cancellationToken)
    {
        var commands = registry.All()
            .Select(descriptor => FrontendCommandCapability.From(
                descriptor,
                registry.GetSource(descriptor.Name)))
            .ToList();
        var catalog = new FrontendCapabilityCatalog(_name, commands);
        await SendAsync(socket, new JsonObject
        {
            ["type"] = "commandCatalog",
            ["catalog"] = JsonSerializer.SerializeToNode(catalog, JsonOptions),
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveEventsAsync(
        ClientWebSocket socket,
        CommandBus localBus,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await WebSocketMessageReader.ReceiveTextAsync(
                socket, buffer, cancellationToken).ConfigureAwait(false);
            if (payload == null)
                return;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                Trace.TraceWarning($"HistoryVulcan 前端忽略无效 WebSocket JSON: {ex.Message}");
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type))
                    continue;

                if (type.GetString() == "uiCommand")
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var text = root.GetProperty("text").GetString() ?? "";
                    var commandResult = await localBus.ExecuteAsync(
                        text,
                        "Service:Relay",
                        cancellationToken).ConfigureAwait(false);
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "commandResult",
                        ["id"] = id,
                        ["success"] = commandResult.Success,
                        ["message"] = commandResult.Message,
                        ["data"] = ToNode(commandResult.Data),
                    }, cancellationToken).ConfigureAwait(false);
                }
                else if (type.GetString() == "confirmation")
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var prompt = root.TryGetProperty("prompt", out var promptValue)
                        ? promptValue.GetString() ?? "确认远程操作？"
                        : "确认远程操作？";
                    var approved = await ConfirmAsync(localBus, prompt).ConfigureAwait(false);
                    await SendAsync(socket, new JsonObject
                    {
                        ["type"] = "confirmationResult",
                        ["id"] = id,
                        ["approved"] = approved,
                    }, cancellationToken).ConfigureAwait(false);
                }
                else if (type.GetString() == "log")
                {
                    var time = root.TryGetProperty("time", out var timeValue)
                               && DateTime.TryParse(timeValue.GetString(), out var parsedTime)
                        ? parsedTime
                        : DateTime.Now;
                    var level = root.TryGetProperty("level", out var levelValue)
                                && Enum.TryParse<ShellLogLevel>(levelValue.GetString(), true, out var parsedLevel)
                        ? parsedLevel
                        : ShellLogLevel.Info;
                    var category = root.TryGetProperty("category", out var categoryValue)
                        ? categoryValue.GetString() ?? "service"
                        : "service";
                    var message = root.TryGetProperty("message", out var messageValue)
                        ? messageValue.GetString() ?? ""
                        : "";
                    LogReceived?.Invoke(this, new ShellLogEntry(time, level, category, message));
                }
                else if (type.GetString() == "moduleRevision")
                {
                    var revision = root.TryGetProperty("revision", out var revisionValue)
                        ? revisionValue.GetInt64()
                        : 0;
                    ModuleRevisionReceived?.Invoke(revision);
                }
            }
        }
    }

    private static Task<bool> ConfirmAsync(CommandBus bus, string prompt)
    {
        var confirmation = bus.Confirmation;
        if (confirmation == null)
            return Task.FromResult(false);
        if (bus.UiContext == null || SynchronizationContext.Current == bus.UiContext)
            return Task.FromResult(confirmation.Confirm(prompt));

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bus.UiContext.Post(_ =>
        {
            try { completion.TrySetResult(confirmation.Confirm(prompt)); }
            catch { completion.TrySetResult(false); }
        }, null);
        return completion.Task;
    }

    private static async Task SendAsync(
        ClientWebSocket socket,
        JsonNode payload,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Provides this HistoryVulcan public contract member.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _lifetime.Cancel();
        _events?.Dispose();
        _http.Dispose();
        _httpHandler.Dispose();
        _lifetime.Dispose();
    }

    private static JsonNode? ToNode(object? data)
    {
        if (data == null)
            return null;
        try
        {
            return JsonSerializer.SerializeToNode(data, JsonOptions);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static string CommandName(string text)
    {
        try
        {
            return CommandParser.Parse(text).Name;
        }
        catch (CommandSyntaxException)
        {
            return text.Trim().Split(' ', 2)[0].ToLowerInvariant();
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        var token = _profile.AccessTokenProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private void SetState(ShellConnectionState state)
    {
        if (State == state)
            return;
        State = state;
        StateChanged?.Invoke(state);
    }

    private sealed record ResultEnvelope(bool Success, string? Message, JsonElement? Data);

    private sealed record RateLimitEnvelope(string? Error, int? RetryAfterSeconds);

    private sealed record HealthEnvelope(
        string ServerId,
        string ProductVersion,
        string HistoryVulcanProtocolVersion,
        string MinClientVersion,
        string[] Capabilities);
}
