
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Clients;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;

using Xunit;

namespace HistoryVulcan.Tests;

[Collection(TestCollections.Gateway)]
public sealed class FreezeBlockerTests
{
    [Fact]
    public async Task SensitiveCommandFormsAndResultsAreRedacted()
    {
        var registry = new CommandRegistry();
        registry.Register(SecretDescriptor("secure.set", "token", position: null));
        registry.Register(SecretDescriptor("secure.key", "private_key", position: null));
        registry.Register(SecretDescriptor("secure.connect", "connectionString", position: null));
        registry.Register(SecretDescriptor("secure.position", "clientSecret", position: 0));
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.app.set",
            Summary = "set",
            Parameters =
            [
                new ParameterSpec { Name = "key", Description = "key", Required = true, Position = 0 },
                new ParameterSpec { Name = "value", Description = "value", Required = true, Position = 1 },
            ],
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(
                $"{context.RequireString("key")} = {context.RequireString("value")}")),
        });
        registry.Register(SecretDescriptor("vulcan.web.token", "value", position: 0));
        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);

        var named = await bus.ExecuteAsync("secure.set token=alpha-secret", "Test");
        var privateKey = await bus.ExecuteAsync("secure.key private_key=private-key-secret", "Test");
        var connection = await bus.ExecuteAsync(
            "secure.connect connectionString=connection-string-secret", "Test");
        var genericPositional = await bus.ExecuteAsync("secure.position positional-secret", "Test");
        var setting = await bus.ExecuteAsync("vulcan.app.set key=mcp.token value=bravo-secret", "Test");
        var positional = await bus.ExecuteAsync("vulcan.web.token charlie-secret", "Test");
        var malformed = await bus.ExecuteAsync("secure.set token=\"fallback-secret", "Test");
        var malformedPositional = await bus.ExecuteAsync(
            "secure.position \"positional-fallback-secret", "Test");
        var malformedToken = await bus.ExecuteAsync("vulcan.web.token \"token-fallback-secret", "Test");
        var malformedSetting = await bus.ExecuteAsync(
            "vulcan.app.set mcp.token \"setting-fallback-secret", "Test");

        var written = string.Join('\n', log.Snapshot().Select(entry => entry.Message));
        Assert.DoesNotContain("alpha-secret", named.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-secret", privateKey.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-string-secret", connection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("positional-secret", genericPositional.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo-secret", setting.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("charlie-secret", positional.Message, StringComparison.Ordinal);
        Assert.False(malformed.Success);
        Assert.False(malformedPositional.Success);
        Assert.False(malformedToken.Success);
        Assert.False(malformedSetting.Success);
        Assert.Null(named.Data);
        Assert.Null(privateKey.Data);
        Assert.Null(connection.Data);
        Assert.Null(genericPositional.Data);
        Assert.Null(positional.Data);
        Assert.DoesNotContain("alpha-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("private-key-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-string-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("positional-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("bravo-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("charlie-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("fallback-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("positional-fallback-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("token-fallback-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("setting-fallback-secret", written, StringComparison.Ordinal);
        Assert.True(log.Snapshot().Count(entry => entry.Message.Contains("[REDACTED]", StringComparison.Ordinal)) >= 9);
    }


    [Fact]
    public async Task CommandExceptionsDoNotExposeSensitiveValues()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "secure.fail",
            Summary = "fail",
            Parameters =
            [
                new ParameterSpec { Name = "token", Description = "token", Required = true },
            ],
            Handler = _ => throw new InvalidOperationException("handler leaked unrelated-internal-secret"),
        });
        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);

        var result = await bus.ExecuteAsync("secure.fail token=input-secret", "Test");

        Assert.False(result.Success);
        Assert.DoesNotContain("input-secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated-internal-secret", result.Message, StringComparison.Ordinal);
        var written = string.Join('\n', log.Snapshot().Select(entry => entry.Message));
        Assert.DoesNotContain("input-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "unrelated-internal-secret",
            written,
            StringComparison.Ordinal);
        Assert.Contains(log.Snapshot(), entry => entry.Category == "cmd:internal");
    }

    // 4.3.0：Web 网关的三条边界用例（回环 Shell 鉴权、凭据每次监听换发、请求体 1 MiB 上限）
    // 随网关迁往 HistoryPortunus/b-Code-Verify/Contracts。同批删除
    // SessionSourceRoundTripsArbitraryIdsAndNames——它测的解码半边 SessionIdFromSource
    // 已无生产调用方，只剩这条测试在维持它活着。
    //
    // 3.13.0 退役 AuthenticationAttemptsAreRateLimitedByRemoteAddressBeforeSessionHeaders：
    // 它守的是"轮换 X-Session-Id 不能绕过失败鉴权的按源限流"。令牌鉴权与限流都随局域网面
    // 一起删除后，未授权请求只会稳定拿到 401。代价是失败鉴权不再有节流——在只监听
    // 127.0.0.1 的前提下可以接受：能对回环发请求的人已经以当前用户身份在执行代码。
    // 一旦将来重新对外监听，这条限流必须与监听能力同时回来。
    // 跨进程全局 mutex：本机若有 HistoryVulcan 实例在跑就会一直等不到，
    // 超时必须是一条具名失败，而不是整轮静默挂死（DEC-023）。
    [Fact(Timeout = 30_000)]
    public async Task ServiceHostWaitsForRestartingPredecessorToReleaseMutex()
    {
        var name = $"Local\\HistoryVulcan.Tests.{Guid.NewGuid():N}";
        using var owner = new Mutex(initiallyOwned: true, name, out var created);
        Assert.True(created);
        var method = typeof(HistoryVulcan.ServiceHost.ServiceHost).GetMethod(
            "WaitForSingleInstance", BindingFlags.NonPublic | BindingFlags.Static)!;
        var waiter = Task.Run(() =>
        {
            using var successor = new Mutex(initiallyOwned: false, name);
            var acquired = Assert.IsType<bool>(method.Invoke(
                null, [successor, TimeSpan.FromSeconds(2)]));
            if (acquired)
                successor.ReleaseMutex();
            return acquired;
        });

        Thread.Sleep(150);
        owner.ReleaseMutex();

        Assert.True(await waiter);
    }




    private static CommandDescriptor SecretDescriptor(string name, string parameter, int? position)
        => new()
        {
            Name = name,
            Summary = "secret",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = parameter,
                    Description = parameter,
                    Required = true,
                    Position = position,
                },
            ],
            Handler = CommandDescriptor.Sync(context =>
            {
                var value = context.RequireString(parameter);
                return CommandResult.Ok($"set {value}", new { value });
            }),
        };

    private static async Task SendTextAsync(WebSocket socket, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var payload = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return JsonDocument.Parse(payload.ToArray());
        }
    }

    private static async Task<HttpResponseMessage> PostCommandAsync(HttpClient client, string command)
    {
        using var body = new StringContent(
            JsonSerializer.Serialize(new { text = command }), Encoding.UTF8, "application/json");
        return await client.PostAsync("api/command", body);
    }

    private static void ConfigureShellSocket(
        ClientWebSocket socket, string sessionId, string name, string accessToken)
    {
        socket.Options.SetRequestHeader("X-HistoryVulcan-Client", "Shell");
        socket.Options.SetRequestHeader("X-Client-Name", name);
        socket.Options.SetRequestHeader("X-Session-Id", sessionId);
        socket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
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


    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.UtcNow, level, category, message);
            lock (_entries)
                _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot()
        {
            lock (_entries)
                return _entries.ToList();
        }
    }
}
