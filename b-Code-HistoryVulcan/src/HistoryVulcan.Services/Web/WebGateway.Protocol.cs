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
using HistoryVulcan.Core.Clients;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Storage;

namespace HistoryVulcan.Services.Web;
public sealed partial class WebGateway : IDisposable
{
    private async Task HandlePairingAsync(HttpListenerContext context)
    {
        var remoteAddress = context.Request.RemoteEndPoint?.Address;
        if (remoteAddress == null
            || (!IPAddress.IsLoopback(remoteAddress) && !context.Request.IsSecureConnection))
        {
            await WriteJsonAsync(context, new { error = "TLS required" }, 403).ConfigureAwait(false);
            return;
        }
        if (DevicePairing == null)
        {
            await WriteJsonAsync(context, new { error = "pairing unavailable" }, 503).ConfigureAwait(false);
            return;
        }

        var request = await ReadJsonAsync<PairRequest>(context.Request).ConfigureAwait(false);
        if (request == null || string.IsNullOrWhiteSpace(request.Code)
            || string.IsNullOrWhiteSpace(request.DeviceId)
            || string.IsNullOrWhiteSpace(request.DeviceName))
        {
            await WriteJsonAsync(context, new { error = "code, deviceId and deviceName are required" }, 400)
                .ConfigureAwait(false);
            return;
        }

        var result = DevicePairing.Pair(
            request.Code,
            request.DeviceId,
            request.DeviceName,
            remoteAddress.ToString());
        await WriteJsonAsync(context, result, result.Success ? 200 : 401).ConfigureAwait(false);
    }

    private static bool CanExecute(
        ClientSession session,
        CommandRegistry registry,
        string text,
        out string denial)
    {
        denial = "";
        if (session.AuthSubject == "loopback-shell" || session.Scopes.Contains("admin")
            || session.Scopes.Contains("operate"))
            return true;
        if (!session.Scopes.Contains("read"))
        {
            denial = "设备没有命令权限";
            return false;
        }
        try
        {
            var parsed = CommandParser.Parse(text);
            if (!registry.TryGet(parsed.Name, out var descriptor))
            {
                denial = $"未知指令: {parsed.Name}";
                return false;
            }
            if (descriptor.Readonly)
                return true;
            denial = $"设备 scope=read 不允许执行 {descriptor.Name}";
            return false;
        }
        catch (CommandSyntaxException ex)
        {
            denial = $"指令语法错误: {ex.Message}";
            return false;
        }
    }

    private static bool IsTrustedLoopbackShell(ClientSession session)
        => session.Kind == ClientKind.Shell
           && session.IsLoopback
           && string.Equals(session.AuthSubject, "loopback-shell", StringComparison.Ordinal);

    private static string SessionSource(ClientSession session)
        => $"{(session.IsLoopback ? session.Kind.ToString() : "Lan" + session.Kind)}:" +
           $"v1.{Base64UrlEncode(session.Id)}:{session.Name}";

    private static string? SessionIdFromSource(string source)
    {
        var first = source.IndexOf(':');
        if (first < 0)
            return null;
        var second = source.IndexOf(':', first + 1);
        if (second < 0)
            return null;
        var encoded = source[(first + 1)..second];
        if (!encoded.StartsWith("v1.", StringComparison.Ordinal))
            return encoded.Length > 0 ? encoded : null;
        try
        {
            return Base64UrlDecode(encoded[3..]);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string StableSessionId(ClientKind kind, IPAddress? address, string? name)
    {
        var seed = $"{kind}\n{address}\n{name ?? kind.ToString()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static string Base64UrlEncode(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static string? ReadBearer(HttpListenerRequest request)
    {
        const string prefix = "Bearer ";
        var header = request.Headers["Authorization"];
        return header?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? header[prefix.Length..].Trim()
            : null;
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private bool AllowRequest(string sessionId)
    {
        var limit = Math.Clamp(_settings.GetInt(KeyRateLimit, 120), 10, 10_000);
        var now = DateTimeOffset.UtcNow;
        var window = _rateWindows.AddOrUpdate(
            sessionId,
            _ => new RateWindow(now, 1),
            (_, current) => now - current.Start >= TimeSpan.FromMinutes(1)
                ? new RateWindow(now, 1)
                : current with { Count = current.Count + 1 });
        TrimRateWindows(now, sessionId);
        return window.Count <= limit;
    }

    private async Task WriteRateLimitAsync(HttpListenerContext context, string key)
    {
        var retryAfterSeconds = 60;
        if (_rateWindows.TryGetValue(key, out var window))
        {
            retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(
                (window.Start.AddMinutes(1) - DateTimeOffset.UtcNow).TotalSeconds));
        }

        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        await WriteJsonAsync(context, new
        {
            error = "rate limit exceeded",
            retryAfterSeconds,
        }, 429).ConfigureAwait(false);
    }

    private bool IsRateLimitReached(string key)
    {
        if (!_rateWindows.TryGetValue(key, out var window))
            return false;
        if (DateTimeOffset.UtcNow - window.Start >= TimeSpan.FromMinutes(1))
        {
            _rateWindows.TryRemove(key, out _);
            return false;
        }
        var limit = Math.Clamp(_settings.GetInt(KeyRateLimit, 120), 10, 10_000);
        return window.Count >= limit;
    }

    private void TrimRateWindows(DateTimeOffset now, string keepSessionId)
    {
        var limit = Math.Clamp(
            _settings.GetInt(KeyRateWindowLimit, DefaultRateWindowLimit), 128, 65_536);
        if (_rateWindows.Count <= limit)
            return;

        foreach (var item in _rateWindows.Where(item =>
                     now - item.Value.Start >= TimeSpan.FromMinutes(1)).ToList())
            _rateWindows.TryRemove(item.Key, out _);
        foreach (var key in _rateWindows.Keys
                     .Where(key => !key.Equals(keepSessionId, StringComparison.Ordinal))
                     .OrderBy(key => key, StringComparer.Ordinal)
                     .Take(Math.Max(0, _rateWindows.Count - limit))
                     .ToList())
            _rateWindows.TryRemove(key, out _);
    }

    private static int DeriveDefaultPort(string appName)
    {
        var hash = 2166136261u;
        foreach (var character in appName.Trim().ToUpperInvariant())
            hash = (hash ^ character) * 16777619u;
        return DefaultPortBase + (int)(hash % DefaultPortSpan);
    }

    private void ApplyCors(HttpListenerContext context)
    {
        var origin = context.Request.Headers["Origin"];
        var configured = _settings.Get(KeyCors);
        if (string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(configured))
            return;

        var allowed = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!allowed.Contains(origin, StringComparer.OrdinalIgnoreCase))
            return;

        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers["Vary"] = "Origin";
        context.Response.Headers["Access-Control-Allow-Headers"] =
            "Authorization, Content-Type, X-Client-Name, X-Session-Id, X-Device-Id";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    }

    private void OnLogEntry(object? sender, ShellLogEntry entry)
    {
        var payload = new JsonObject
        {
            ["type"] = "log",
            ["time"] = entry.Time.ToString("O"),
            ["level"] = entry.Level.ToString(),
            ["category"] = entry.Category,
            ["message"] = entry.Message,
        };
        foreach (var client in _clients.Values)
        {
            if ((entry.Category.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                 || entry.Category.StartsWith(CommandBus.EchoCategoryPrefix, StringComparison.OrdinalIgnoreCase))
                && !client.Session.Scopes.Contains("operate")
                && !client.Session.Scopes.Contains("admin"))
                continue;
            client.TryQueueLog(payload);
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        var body = await HttpRequestBodyReader.ReadUtf8Async(request).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, object value, int status)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task SendAsync(EventClient client, JsonNode payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
        await client.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            client.SendLock.Release();
        }
    }

    private static async Task SendIgnoringErrorsAsync(EventClient client, JsonNode payload)
    {
        try
        {
            if (client.Socket.State == WebSocketState.Open)
                await SendAsync(client, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string NormalizeBind(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? "127.0.0.1"
            : value.Trim();

    private static bool IsLoopback(string value)
        => value is "127.0.0.1" or "::1" || value.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    private static void TryClose(HttpListenerContext context, int status)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class EventClient : IDisposable
    {
        private const int LogQueueCapacity = 256;
        private int _disposed;
        private readonly Channel<JsonObject> _logQueue;
        private readonly Task _logPump;

        public EventClient(ClientSession session, WebSocket socket)
        {
            Session = session;
            Socket = socket;
            _logQueue = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(LogQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });
            _logPump = Task.Run(PumpLogsAsync);
        }

        public ClientSession Session { get; }

        public WebSocket Socket { get; }

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public bool TryQueueLog(JsonObject payload)
            => Volatile.Read(ref _disposed) == 0
               && _logQueue.Writer.TryWrite((JsonObject)payload.DeepClone());

        private async Task PumpLogsAsync()
        {
            await foreach (var payload in _logQueue.Reader.ReadAllAsync().ConfigureAwait(false))
                await SendIgnoringErrorsAsync(this, payload).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _logQueue.Writer.TryComplete();
            Socket.Dispose();
            SendLock.Dispose();
            _ = _logPump.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed record CommandRequest(string Text);

    private sealed record ConfirmRequest(string Id, bool Approved);

    private sealed record PairRequest(string Code, string DeviceId, string DeviceName);

    private sealed record RateWindow(DateTimeOffset Start, int Count);

    private sealed record PendingCommand(
        string SessionId,
        TaskCompletionSource<CommandResult> Completion);

    private sealed record PendingConfirmation(
        string SessionId,
        TaskCompletionSource<bool> Completion);
}

