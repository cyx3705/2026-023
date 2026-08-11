using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Web;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(TestCollections.Gateway)]
public sealed class WebGatewayRateLimitTests
{
    [Fact]
    public async Task AuthenticatedLoopbackShellDoesNotConsumePublicWebQuota()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "health.ping",
            Summary = "ping",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
        });
        var settings = new MemorySettings();
        settings.Set(WebGateway.KeyRateLimit, "10");
        var bus = new CommandBus(registry, new NullLog());
        using var gateway = new WebGateway(() => bus, settings, new NullLog());
        Assert.True(gateway.Start(FreePort()).Success);
        using var client = new ShellServiceClient(
            new Uri($"http://127.0.0.1:{gateway.Port}/"), "RateLimitFrontend");

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var result = await client.ExecuteAsync("health.ping", "Test");
            Assert.True(result.Success, $"attempt={attempt + 1}: {result.Message}");
        }
    }

    [Fact]
    public async Task AuthenticatedWebSessionRemainsLimitedAndReportsRetryWindow()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "health.ping",
            Summary = "ping",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
        });
        var settings = new MemorySettings();
        settings.Set(WebGateway.KeyToken, "web-secret");
        settings.Set(WebGateway.KeyRateLimit, "10");
        var bus = new CommandBus(registry, new NullLog());
        using var gateway = new WebGateway(() => bus, settings, new NullLog());
        Assert.True(gateway.Start(FreePort()).Success);
        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/"),
        };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "web-secret");
        client.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var accepted = await PostCommandAsync(client);
            Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        }

        using var limited = await PostCommandAsync(client);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter?.Delta);
        using var payload = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("rate limit exceeded", payload.RootElement.GetProperty("error").GetString());
        Assert.InRange(payload.RootElement.GetProperty("retryAfterSeconds").GetInt32(), 1, 60);
    }

    [Fact]
    public async Task ShellClientExplainsHttp429RetryWindow()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var responseTask = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            var bytes = Encoding.UTF8.GetBytes(
                """{"error":"rate limit exceeded","retryAfterSeconds":17}""");
            context.Response.StatusCode = 429;
            context.Response.Headers["Retry-After"] = "17";
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        });
        using var client = new ShellServiceClient(
            new Uri($"http://127.0.0.1:{port}/"), "RateLimitMessage");

        var result = await client.ExecuteAsync("health.ping", "Test");
        await responseTask;

        Assert.False(result.Success);
        Assert.Contains("HTTP 429", result.Message, StringComparison.Ordinal);
        Assert.Contains("17 秒", result.Message, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostCommandAsync(HttpClient client)
    {
        var json = JsonSerializer.Serialize(new { text = "health.ping", source = "Test" });
        return client.PostAsync(
            "api/command",
            new StringContent(json, Encoding.UTF8, "application/json"));
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
