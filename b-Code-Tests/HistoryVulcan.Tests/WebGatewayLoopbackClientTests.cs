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
public sealed class WebGatewayLoopbackClientTests
{
    /// <summary>
    /// 3.13.0 前这条用例叫 AuthenticatedLoopbackShellDoesNotConsumePublicWebQuota，
    /// 守的是"前端不吃公共 Web 配额"。限流随局域网面删除后不再有配额可言，
    /// 但"前端可以连续高频调用而不被任何中间层拦下"仍然是要守的行为，故保留。
    /// </summary>
    [Fact]
    public async Task LoopbackShellSustainsRapidSequentialCommands()
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
        var bus = new CommandBus(registry, new NullLog());
        using var gateway = new WebGateway(() => bus, settings, new NullLog());
        Assert.True(gateway.Start(FreePort()).Success);
        using var client = new ShellServiceClient(
            ConnectedProfile(gateway), "RateLimitFrontend");

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var result = await client.ExecuteAsync("health.ping", "Test");
            Assert.True(result.Success, $"attempt={attempt + 1}: {result.Message}");
        }
    }

    // 3.13.0 退役 AuthenticatedWebSessionRemainsLimitedAndReportsRetryWindow：
    // 令牌鉴权的 Web 会话已无法建立（见 FreezeBlockerTests.GatewayAcceptsOnlyTheLoopbackShellSession），
    // 网关自身也不再产生 429。下面这条保留：它验证的是**客户端**遇到 429 时的解释能力，
    // 用的是独立的假监听器，与网关是否限流无关——反向代理等中间层仍可能返回 429。
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

    /// <summary>构造一个持有本次监听 IPC 凭据的前端配置（3.13.0 起前端必须持券）。</summary>
    internal static ShellEndpointProfile ConnectedProfile(WebGateway gateway)
        => new(
            new Uri($"http://127.0.0.1:{gateway.Port}/"),
            Guid.NewGuid().ToString("N"),
            AccessTokenProvider: () => gateway.AccessToken);

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
