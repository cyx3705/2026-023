
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
using HistoryVulcan.Services.Web;
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

    /// <summary>
    /// 3.13.0 用这条不变量取代原先的两个只读设备会话用例
    /// （<c>ReadOnlyWebSessionDoesNotReceiveCommandLogs</c> 与
    /// <c>ReadOnlyWebSessionFailsClosedForUnknownMalformedAndWritableCommands</c>）。
    ///
    /// 那两个用例守的是"设备 scope=read 时不得执行可写指令"，前提是网关能产生低于 admin 的
    /// 会话。删除局域网面后这个前提消失了：唯一能通过鉴权的是同机前端 Shell。因此要守的边界
    /// 从"部分授权会话不能越权"变成"除同机 Shell 外任何人都进不来"——退化成一条更强的约束，
    /// 但必须显式验证，否则删除 scope 判定就成了无人看守的放宽。
    /// </summary>
    [Fact]
    public async Task GatewayAcceptsOnlyTheLoopbackShellSession()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "unsafe.write",
            Summary = "write",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("must-not-run")),
        });
        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);
        using var gateway = new WebGateway(() => bus, new MemorySettings(), log);
        Assert.True(gateway.Start(FreePort()).Success);

        // 不声明 X-HistoryVulcan-Client: Shell 的回环调用方按 ClientKind.Web 归类，一律 401。
        using var web = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        web.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
        web.DefaultRequestHeaders.Add("X-Client-Name", "PlainWeb");
        using var rejected = await PostCommandAsync(web, "unsafe.write");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // 携带过去的设备鉴权头也不再有任何特权路径可走。
        using var device = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        device.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "read-token");
        device.DefaultRequestHeaders.Add("X-Device-Id", "read-device");
        device.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
        using var deviceRejected = await PostCommandAsync(device, "unsafe.write");
        Assert.Equal(HttpStatusCode.Unauthorized, deviceRejected.StatusCode);

        // 只伪造 Shell 头、不持券的本机进程必须被挡下。这是本条用例的核心：
        // 回环与请求头都可以被任意本机进程伪造，真正的边界只有一次性凭据。
        using var forged = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        forged.DefaultRequestHeaders.Add("X-HistoryVulcan-Client", "Shell");
        forged.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
        forged.DefaultRequestHeaders.Add("X-Client-Name", "ForgedFrontend");
        using var forgedRejected = await PostCommandAsync(forged, "unsafe.write");
        Assert.Equal(HttpStatusCode.Unauthorized, forgedRejected.StatusCode);

        // 持错券同样被挡下。
        using var wrongToken = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        wrongToken.DefaultRequestHeaders.Add("X-HistoryVulcan-Client", "Shell");
        wrongToken.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
        wrongToken.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gateway.AccessToken + "x");
        using var wrongRejected = await PostCommandAsync(wrongToken, "unsafe.write");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRejected.StatusCode);

        // 持本次监听凭据的同机前端畅通，且拿到完整权限。
        using var shell = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        shell.DefaultRequestHeaders.Add("X-HistoryVulcan-Client", "Shell");
        shell.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
        shell.DefaultRequestHeaders.Add("X-Client-Name", "Frontend");
        shell.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gateway.AccessToken);
        using var accepted = await PostCommandAsync(shell, "unsafe.write");
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    /// <summary>
    /// 凭据必须每次监听换发，否则 endpoint.json 的残留值会在宿主重启后继续有效。
    /// </summary>
    [Fact]
    public void AccessTokenIsRegeneratedPerListenAndClearedOnStop()
    {
        var log = new MemoryLog();
        using var gateway = new WebGateway(
            () => new CommandBus(new CommandRegistry(), log), new MemorySettings(), log);

        Assert.True(gateway.Start(FreePort()).Success);
        var first = gateway.AccessToken;
        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.True(first.Length >= 32);

        Assert.True(gateway.Stop().Success);
        Assert.Equal("", gateway.AccessToken);

        Assert.True(gateway.Start(FreePort()).Success);
        Assert.NotEqual(first, gateway.AccessToken);
    }

    [Fact]
    public async Task DuplicateWebSocketSessionIdDoesNotReplaceTheOriginalClient()
    {
        var log = new MemoryLog();
        using var gateway = new WebGateway(
            () => new CommandBus(new CommandRegistry(), log),
            new MemorySettings(),
            log);
        Assert.True(gateway.Start(FreePort()).Success);
        var sessionId = Guid.NewGuid().ToString("N");

        using var original = new ClientWebSocket();
        ConfigureShellSocket(original, sessionId, "Original", gateway.AccessToken);
        await original.ConnectAsync(
            new Uri($"ws://127.0.0.1:{gateway.Port}/api/events"), CancellationToken.None);
        using (var connected = await ReceiveJsonAsync(original))
            Assert.Equal("connected", connected.RootElement.GetProperty("type").GetString());

        using var duplicate = new ClientWebSocket();
        ConfigureShellSocket(duplicate, sessionId, "Duplicate", gateway.AccessToken);
        await duplicate.ConnectAsync(
            new Uri($"ws://127.0.0.1:{gateway.Port}/api/events"), CancellationToken.None);
        using (var rejected = await ReceiveJsonAsync(duplicate))
        {
            Assert.Equal("error", rejected.RootElement.GetProperty("type").GetString());
            Assert.Equal("session_id_in_use", rejected.RootElement.GetProperty("error").GetString());
        }

        log.Info("app", "original-still-connected");
        using var received = await ReceiveJsonAsync(original);
        Assert.Equal("original-still-connected", received.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task WebGatewayRejectsRequestBodiesOverOneMiBBeforeDeserialization()
    {
        var gateway = new WebGateway(
            () => new CommandBus(new CommandRegistry(), new MemoryLog()),
            new MemorySettings(),
            new MemoryLog());
        using (gateway)
        {
            Assert.True(gateway.Start(FreePort()).Success);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            client.DefaultRequestHeaders.Add("X-HistoryVulcan-Client", "Shell");
            client.DefaultRequestHeaders.Add("X-Client-Name", "LargeBodyTest");
            client.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", gateway.AccessToken);
            using var body = new StringContent(
                new string('x', 1_048_577), Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("api/command", body);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    // 3.13.0 退役 AuthenticationAttemptsAreRateLimitedByRemoteAddressBeforeSessionHeaders：
    // 它守的是"轮换 X-Session-Id 不能绕过失败鉴权的按源限流"。令牌鉴权与限流都随局域网面
    // 一起删除后，未授权请求只会稳定拿到 401。代价是失败鉴权不再有节流——在只监听
    // 127.0.0.1 的前提下可以接受：能对回环发请求的人已经以当前用户身份在执行代码。
    // 一旦将来重新对外监听，这条限流必须与监听能力同时回来。
    [Theory]
    [InlineData("Web:127.0.0.1:Web", "浏览器")]
    [InlineData("含:冒号:与中文", "中文:名称")]
    [InlineData("plain-id", "")]
    public void SessionSourceRoundTripsArbitraryIdsAndNames(string id, string name)
    {
        var session = new ClientSession(
            id, ClientKind.Web, name, "3.0.0", DateTimeOffset.UtcNow)
        {
            IsLoopback = true,
        };
        var sourceMethod = typeof(WebGateway).GetMethod(
            "SessionSource", BindingFlags.NonPublic | BindingFlags.Static)!;
        var idMethod = typeof(WebGateway).GetMethod(
            "SessionIdFromSource", BindingFlags.NonPublic | BindingFlags.Static)!;

        var source = Assert.IsType<string>(sourceMethod.Invoke(null, [session]));
        var recovered = Assert.IsType<string>(idMethod.Invoke(null, [source]));

        Assert.Equal(id, recovered);
    }

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
