
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Clients;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.Services.Web;
using HistoryVulcan.Shell;
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
    public async Task SecretSettingValuesAreMaskedInCommandResultsNotOnlyInLogs()
    {
        // 回归 FZR-01 的读路径:vulcan.app.get 声明 Readonly,对 scope=read 的远程设备放行,
        // 并在默认 readonly 策略下作为 MCP 工具可见。结果对象会原样序列化进 HTTP 响应体
        // 与 tools/call 载荷,因此断言必须落在 CommandResult.Message 上,而不只是日志。
        var settings = new MemorySettings();
        settings.Set("web.token", "delta-secret");
        settings.Set("database.connectionString", "connection-secret");
        settings.Set("signing.private_key", "private-secret");
        settings.Set("code", "code-setting-secret");
        settings.Set("console.history", "500");
        var log = new MemoryLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        BuiltinCommands.Register(registry, new ShellCommandServices
        {
            Window = null!,
            Docking = null!,
            Console = null!,
            History = null!,
            Settings = settings,
            Log = log,
            Bus = bus,
            DataDirectory = "",
        });

        var single = await bus.ExecuteAsync("vulcan.app.get key=web.token", "Test");
        var code = await bus.ExecuteAsync("vulcan.app.get key=code", "Test");
        var listing = await bus.ExecuteAsync("vulcan.app.get", "Test");
        var write = await bus.ExecuteAsync("vulcan.app.set key=web.token value=echo-secret", "Test");

        Assert.DoesNotContain("delta-secret", single.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", code.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("delta-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", listing.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("echo-secret", write.Message, StringComparison.Ordinal);
        Assert.Contains("(已配置)", single.Message, StringComparison.Ordinal);
        Assert.Contains("(已配置)", code.Message, StringComparison.Ordinal);

        // 非敏感键不受影响,vulcan.app.get 仍是可用的排查工具
        Assert.Contains("console.history = 500", listing.Message, StringComparison.Ordinal);

        var written = string.Join('\n', log.Snapshot().Select(entry => entry.Message));
        Assert.DoesNotContain("delta-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("connection-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("code-setting-secret", written, StringComparison.Ordinal);
        Assert.DoesNotContain("echo-secret", written, StringComparison.Ordinal);
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

    [Fact]
    public async Task ReadOnlyWebSessionDoesNotReceiveCommandLogs()
    {
        var log = new MemoryLog();
        var gateway = new WebGateway(
            () => new CommandBus(new CommandRegistry(), log),
            new MemorySettings(),
            log)
        {
            DeviceAuthentication = new ReadOnlyAuthentication(),
        };
        using (gateway)
        {
            Assert.True(gateway.Start(FreePort()).Success);
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer read-token");
            socket.Options.SetRequestHeader("X-Device-Id", "read-device");
            socket.Options.SetRequestHeader("X-Client-Name", "ReadOnlyWeb");
            await socket.ConnectAsync(
                new Uri($"ws://127.0.0.1:{gateway.Port}/api/events"), CancellationToken.None);
            using var connected = await ReceiveJsonAsync(socket);
            Assert.Equal("connected", connected.RootElement.GetProperty("type").GetString());

            log.Info("cmd:UI", "must-not-be-broadcast");
            log.Error("cmd", "legacy-command-error-must-not-be-broadcast");
            log.Info("app", "ordinary-log");
            using var received = await ReceiveJsonAsync(socket);
            Assert.Equal("app", received.RootElement.GetProperty("category").GetString());
            Assert.Equal("ordinary-log", received.RootElement.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task ReadOnlyWebSessionFailsClosedForUnknownMalformedAndWritableCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "safe.read",
            Summary = "read",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("read-ok")),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "unsafe.write",
            Summary = "write",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("must-not-run")),
        });
        var log = new MemoryLog();
        var bus = new CommandBus(registry, log);
        using var gateway = new WebGateway(() => bus, new MemorySettings(), log)
        {
            DeviceAuthentication = new ReadOnlyAuthentication(),
        };
        Assert.True(gateway.Start(FreePort()).Success);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "read-token");
        client.DefaultRequestHeaders.Add("X-Device-Id", "read-device");
        client.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));

        using var allowed = await PostCommandAsync(client, "safe.read");
        using var unknown = await PostCommandAsync(client, "late.registered");
        using var malformed = await PostCommandAsync(client, "safe.read \\\"unterminated");
        using var writable = await PostCommandAsync(client, "unsafe.write");

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, writable.StatusCode);
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
        ConfigureShellSocket(original, sessionId, "Original");
        await original.ConnectAsync(
            new Uri($"ws://127.0.0.1:{gateway.Port}/api/events"), CancellationToken.None);
        using (var connected = await ReceiveJsonAsync(original))
            Assert.Equal("connected", connected.RootElement.GetProperty("type").GetString());

        using var duplicate = new ClientWebSocket();
        ConfigureShellSocket(duplicate, sessionId, "Duplicate");
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
            using var body = new StringContent(
                new string('x', 1_048_577), Encoding.UTF8, "application/json");

            using var response = await client.PostAsync("api/command", body);

            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    [Fact]
    public async Task AuthenticationAttemptsAreRateLimitedByRemoteAddressBeforeSessionHeaders()
    {
        var settings = new MemorySettings();
        settings.Set(WebGateway.KeyToken, "correct-token");
        settings.Set(WebGateway.KeyRateLimit, "10");
        var gateway = new WebGateway(
            () => new CommandBus(new CommandRegistry(), new MemoryLog()),
            settings,
            new MemoryLog());
        using (gateway)
        {
            Assert.True(gateway.Start(FreePort()).Success);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gateway.Port}/") };
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-token");
            HttpStatusCode last = 0;
            for (var attempt = 0; attempt < 11; attempt++)
            {
                client.DefaultRequestHeaders.Remove("X-Session-Id");
                client.DefaultRequestHeaders.Add("X-Session-Id", Guid.NewGuid().ToString("N"));
                using var body = new StringContent("{\"text\":\"help\"}", Encoding.UTF8, "application/json");
                using var response = await client.PostAsync("api/command", body);
                last = response.StatusCode;
            }

            Assert.Equal(HttpStatusCode.TooManyRequests, last);
        }
    }

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


    [Fact]
    public void CommandHistoryMigratesLegacySecretsAndStoresOnlyRedactedManualEchoes()
    {
        var root = Path.Combine(Path.GetTempPath(), "HistoryVulcan-history-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var historyPath = Path.Combine(root, "history.txt");
        try
        {
            File.WriteAllText(historyPath, "vulcan.web.token legacy-history-secret\n");
            var history = new CommandHistory(historyPath);
            Assert.Empty(history.Snapshot());
            Assert.DoesNotContain(
                "legacy-history-secret", File.ReadAllText(historyPath), StringComparison.Ordinal);

            Exception? failure = null;
            using var finished = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    var registry = new CommandRegistry();
                    registry.Register(SecretDescriptor("vulcan.web.token", "value", position: 0));
                    registry.Register(SecretDescriptor("secure.position", "clientSecret", position: 0));
                    registry.Register(new CommandDescriptor
                    {
                        Name = "vulcan.app.set",
                        Summary = "set",
                        Parameters =
                        [
                            new ParameterSpec
                            {
                                Name = "key",
                                Description = "key",
                                Required = true,
                                Position = 0,
                            },
                            new ParameterSpec
                            {
                                Name = "value",
                                Description = "value",
                                Required = true,
                                Position = 1,
                            },
                        ],
                        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("set")),
                    });
                    registry.Register(new CommandDescriptor
                    {
                        Name = "safe.read",
                        Summary = "safe",
                        Parameters =
                        [
                            new ParameterSpec
                            {
                                Name = "value",
                                Description = "value",
                                Required = true,
                                Position = 0,
                            },
                        ],
                        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("safe")),
                    });
                    var log = new MemoryLog();
                    var bus = new CommandBus(registry, log);
                    _ = new HistoryVulcan.Shell.Console.ConsoleView(
                        log,
                        bus,
                        history,
                        new HistoryVulcan.Shell.CommandSurface.DeferredCommandCatalogSession());

                    bus.ExecuteAsync("vulcan.web.token console-history-secret", "手动").GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.app.set mcp.token setting-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.web.token \"malformed-token-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("vulcan.app.set mcp.token \"malformed-setting-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("secure.position \"derived-history-secret", "手动")
                        .GetAwaiter().GetResult();
                    bus.ExecuteAsync("safe.read visible-value", "手动").GetAwaiter().GetResult();
                    history.Save();

                    var historyRegistry = new CommandRegistry();
                    var historyBus = new CommandBus(historyRegistry, new MemoryLog());
                    BuiltinCommands.Register(historyRegistry, new ShellCommandServices
                    {
                        Window = null!,
                        Docking = null!,
                        Console = null!,
                        History = history,
                        Settings = new MemorySettings(),
                        Log = new MemoryLog(),
                        Bus = historyBus,
                        DataDirectory = root,
                    });
                    var historyResult = historyBus.ExecuteAsync("vulcan.command.history count=20", "Web:read")
                        .GetAwaiter().GetResult();
                    Assert.True(historyResult.Success, historyResult.Message);
                    Assert.DoesNotContain("console-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("setting-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("malformed-token-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("malformed-setting-history-secret", historyResult.Message, StringComparison.Ordinal);
                    Assert.DoesNotContain("derived-history-secret", historyResult.Message, StringComparison.Ordinal);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    finished.Set();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
            thread.Join();
            if (failure != null)
                throw failure;

            Assert.Equal(
                [
                    "vulcan.web.token [REDACTED]",
                    "vulcan.app.set mcp.token [REDACTED]",
                    "vulcan.web.token [REDACTED]",
                    "vulcan.app.set [REDACTED]",
                    "secure.position [REDACTED]",
                    "safe.read visible-value",
                ],
                history.Snapshot());
            var persisted = File.ReadAllText(historyPath);
            Assert.Contains("# HistoryVulcan.CommandHistory.v2:redacted", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("console-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("setting-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-token-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("malformed-setting-history-secret", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("derived-history-secret", persisted, StringComparison.Ordinal);
            Assert.Contains("visible-value", persisted, StringComparison.Ordinal);
            Assert.Equal(history.Snapshot(), new CommandHistory(historyPath).Snapshot());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }


    [Fact]
    public async Task MalformedWebSocketJsonDoesNotKillFrontendLoop()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = ServeMalformedThenValidAsync(listener);

        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "ui.afterbad",
            Summary = "after bad",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("still-alive")),
        });
        using var client = new ShellServiceClient(
            new Uri($"http://127.0.0.1:{port}/"), "MalformedFrameTest");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = client.RunEventLoopAsync(
            new CommandBus(registry, new MemoryLog()), cancellation.Token);

        using var response = await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(response.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("still-alive", response.RootElement.GetProperty("message").GetString());
        cancellation.Cancel();
        try { await loop; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
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

    private static async Task<JsonDocument> ServeMalformedThenValidAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        var accepted = await context.AcceptWebSocketAsync(null);
        using var socket = accepted.WebSocket;
        using (await ReceiveJsonAsync(socket))
        {
        }
        await SendTextAsync(socket, "{\"type\":");
        await SendTextAsync(socket,
            "{\"type\":\"uiCommand\",\"id\":\"after-bad\",\"text\":\"ui.afterbad\",\"source\":\"Test\"}");
        return await ReceiveJsonAsync(socket);
    }

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

    private static void ConfigureShellSocket(ClientWebSocket socket, string sessionId, string name)
    {
        socket.Options.SetRequestHeader("X-HistoryVulcan-Client", "Shell");
        socket.Options.SetRequestHeader("X-Client-Name", name);
        socket.Options.SetRequestHeader("X-Session-Id", sessionId);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ReadOnlyAuthentication : IDeviceAuthenticationProvider
    {
        public DeviceAuthenticationResult Authenticate(string deviceId, string token, string? remoteAddress)
            => DeviceAuthenticationResult.Accept(deviceId, "read-only", ["read"]);
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
