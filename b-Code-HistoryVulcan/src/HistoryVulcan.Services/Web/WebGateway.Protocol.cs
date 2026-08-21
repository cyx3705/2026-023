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
    private static bool IsTrustedLoopbackShell(ClientSession session)
        => session.Kind == ClientKind.Shell
           && session.IsLoopback
           && string.Equals(session.AuthSubject, "loopback-shell", StringComparison.Ordinal);

    private static string SessionSource(ClientSession session)
        => $"{session.Kind}:v1.{Base64UrlEncode(session.Id)}:{session.Name}";

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
        => LoopbackHttpTransport.ReadBearer(request);

    private static bool FixedEquals(string left, string right)
        => LoopbackHttpTransport.FixedEquals(left, right);

    private static int DeriveDefaultPort(string appName)
        => LoopbackHttpTransport.DerivePort(appName, DefaultPortBase, DefaultPortSpan);

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

    private static void TryClose(HttpListenerContext context, int status)
        => LoopbackHttpTransport.TryClose(context, status);

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

    private sealed record PendingCommand(
        string SessionId,
        TaskCompletionSource<CommandResult> Completion);

}

