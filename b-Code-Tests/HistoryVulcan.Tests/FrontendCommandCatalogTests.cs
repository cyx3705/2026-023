using System.Net;
using System.Net.Sockets;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Web;
using HistoryVulcan.Shell.Mcp;
using Xunit;

namespace HistoryVulcan.Tests;

[Collection(TestCollections.Gateway)]
public sealed class FrontendCommandCatalogTests
{
    [Fact]
    public void WebPortsAreDerivedPerApplicationAndRetryOnConflict()
    {
        using var alpha = CreateGateway("WebAlpha");
        using var beta = CreateGateway("WebBeta");
        using var secondAlpha = CreateGateway("WebAlpha");

        Assert.True(alpha.Start().Success);
        Assert.True(beta.Start().Success);
        Assert.True(secondAlpha.Start().Success);
        Assert.NotEqual(alpha.Port, beta.Port);
        Assert.True(secondAlpha.Port > alpha.Port);
    }

    [Fact]
    public void CapabilityRoundTripPreservesGovernanceMetadata()
    {
        var source = new CommandDescriptor
        {
            Name = "ui.inspect",
            Domain = "InspectorModule",
            CommandClass = "ui",
            Summary = "Inspect the active UI object",
            Example = "ui.inspect mode=brief",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "mode",
                    Description = "Output mode",
                    Type = ParamType.String,
                    Required = true,
                    Position = 0,
                    AllowedValues = ["brief", "full"],
                },
            ],
            Readonly = true,
            Dangerous = true,
            RequiresUiThread = true,
            AllowMcpExecution = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

        var capability = FrontendCommandCapability.From(source, "app:frontend");
        var proxy = capability.CreateProxy();

        Assert.Equal("app:frontend", capability.Source);
        Assert.Equal(source.Name, proxy.Name);
        Assert.Equal(source.Domain, capability.Domain);
        Assert.Equal(source.CommandClass, capability.CommandClass);
        Assert.Equal(source.Domain, proxy.Domain);
        Assert.Equal(source.CommandClass, proxy.CommandClass);
        Assert.Equal(source.Summary, proxy.Summary);
        Assert.Equal(source.Example, proxy.Example);
        Assert.Equal(source.Readonly, proxy.Readonly);
        Assert.Equal(source.IsDangerous, proxy.IsDangerous);
        Assert.Equal(source.RequiresUiThread, proxy.RequiresUiThread);
        Assert.Equal(source.AllowMcpExecution, proxy.AllowMcpExecution);
        Assert.Equal(CommandExecutionSite.Frontend, proxy.ExecutionSite);
        Assert.Equal(source.Parameters.Single().AllowedValues, proxy.Parameters.Single().AllowedValues);
    }

    [Fact]
    public void FrontendCommandsRequireExplicitMcpOptIn()
    {
        var hidden = FrontendCommandCapability.From(
            FrontendDescriptor("ui.hidden", "hidden"), "app:frontend").CreateProxy();
        var visible = FrontendCommandCapability.From(
            FrontendDescriptor("ui.visible", "visible", allowMcpExecution: true),
            "app:frontend").CreateProxy();

        Assert.False(McpExposurePolicy.IsVisible(hidden, "standard"));
        Assert.True(McpExposurePolicy.IsVisible(visible, "standard"));

        var registry = new CommandRegistry();
        registry.Register(visible);
        var schema = new CommandSchemaExporter(registry).Find(visible.Name)!.InputSchema;
        Assert.True(schema["properties"]!["_frontend"] != null);
    }

    [Fact]
    public async Task CatalogSynchronizesWithoutNameListAndRemainsBrowsableOffline()
    {
        using var fixture = GatewayFixture.Start();
        using var client = fixture.Connect("CatalogApp", "catalog.dynamic", "from-catalog");

        await WaitUntilAsync(() => fixture.ServiceRegistry.TryGet("catalog.dynamic", out _));
        Assert.True(fixture.ServiceRegistry.TryGet("catalog.dynamic", out var proxy));
        Assert.Equal("Dynamic frontend command", proxy.Summary);
        Assert.Equal(CommandExecutionSite.Frontend, proxy.ExecutionSite);
        Assert.False(McpExposurePolicy.IsVisible(proxy, "standard"));

        var online = await fixture.ServiceBus.ExecuteAsync("catalog.dynamic", "Test");
        Assert.True(online.Success);
        Assert.Equal("from-catalog", online.Message);

        client.Stop();
        await WaitUntilAsync(() => fixture.Gateway.ConnectedShells == 0);

        Assert.True(fixture.ServiceRegistry.TryGet("catalog.dynamic", out _));
        var offline = await fixture.ServiceBus.ExecuteAsync("catalog.dynamic", "Test");
        Assert.False(offline.Success);
        Assert.Contains("前端", offline.Message);
    }

    [Fact]
    public async Task ServiceCommandCatalogIncludesModuleAndFrontendCapabilities()
    {
        using var fixture = GatewayFixture.Start(withCatalog: true);
        fixture.ServiceRegistry.Register(new CommandDescriptor
        {
            Name = "sample.inspect",
            Domain = "SampleModule",
            CommandClass = "diagnostic",
            Summary = "Inspect a sample module",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("module")),
        }, "module:SampleModule");

        using var client = fixture.Connect("CatalogApp", "catalog.dynamic", "from-catalog");
        await WaitUntilAsync(() => fixture.ServiceRegistry.TryGet("catalog.dynamic", out _));

        var combined = await fixture.ServiceBus.ExecuteAsync("vulcan.command.list", "Test");

        Assert.True(combined.Success, combined.Message);
        Assert.Contains("sample.inspect", combined.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("catalog.dynamic", combined.Message, StringComparison.OrdinalIgnoreCase);

        var moduleOnly = await fixture.ServiceBus.ExecuteAsync(
            "vulcan.command.list domain=SampleModule class=diagnostic", "Test");
        Assert.True(moduleOnly.Success, moduleOnly.Message);
        Assert.Contains("sample.inspect", moduleOnly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog.dynamic", moduleOnly.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleFrontendsRequireDeterministicTarget()
    {
        using var fixture = GatewayFixture.Start();
        using var alpha = fixture.Connect("Alpha", "ui.route", "alpha");
        using var beta = fixture.Connect("Beta", "ui.route", "beta");

        await WaitUntilAsync(() => fixture.Gateway.ConnectedShells == 2
                                   && fixture.ServiceRegistry.TryGet("ui.route", out _));

        var ambiguous = await fixture.ServiceBus.ExecuteAsync("ui.route", "Test");
        Assert.False(ambiguous.Success);
        Assert.Contains("_frontend", ambiguous.Message);

        var selected = await fixture.ServiceBus.ExecuteAsync("ui.route _frontend=Beta", "Test");
        Assert.True(selected.Success);
        Assert.Equal("beta", selected.Message);
    }

    [Fact]
    public async Task FragmentedLargeFrontendCommandIsExecutedAndAcknowledged()
    {
        using var fixture = GatewayFixture.Start();
        var descriptor = new CommandDescriptor
        {
            Name = "ui.large",
            Summary = "Process a large frontend payload",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "payload",
                    Description = "Large payload",
                    Required = true,
                },
            ],
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(
                context.RequireString("payload").Length.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        };
        using var client = fixture.Connect("LargeFrontend", descriptor);
        await WaitUntilAsync(() => fixture.ServiceRegistry.TryGet("ui.large", out _));
        var payload = new string('x', 100_000);

        var result = await fixture.ServiceBus.ExecuteAsync(
            $"ui.large payload={CommandParser.QuoteArg(payload)}", "Test");

        Assert.True(result.Success, result.Message);
        Assert.Equal("100000", result.Message);
    }

    [Fact]
    public async Task FocusConsoleServiceCommandRelaysToConnectedFrontend()
    {
        using var fixture = GatewayFixture.Start();
        var composition = new ServiceComposition
        {
            ServiceName = "HistoryVulcan.Test",
            Registry = fixture.ServiceRegistry,
            Bus = fixture.ServiceBus,
            Settings = new MemorySettings(),
            Log = new NullLog(),
            Web = fixture.Gateway,
        };
        ServiceCommands.RegisterAll(
            fixture.ServiceRegistry,
            composition,
            requestStop: static () => { },
            executablePath: "HistoryVulcan.exe");

        Assert.True(fixture.ServiceRegistry.TryGet("vulcan.app.focusconsole", out var serviceCommand));
        Assert.Equal("app", serviceCommand.CommandClass);

        using var client = fixture.Connect(
            "FocusFrontend",
            "vulcan.app.focusconsole",
            "focused");
        await WaitUntilAsync(() => fixture.Gateway.ConnectedShells == 1);

        var result = await fixture.ServiceBus.ExecuteAsync("vulcan.app.focusconsole", "Test");

        Assert.True(result.Success, result.Message);
        Assert.Equal("focused", result.Message);
    }

    private static CommandDescriptor FrontendDescriptor(
        string name,
        string result,
        bool allowMcpExecution = false) => new()
        {
            Name = name,
            Summary = "Dynamic frontend command",
            Readonly = true,
            AllowMcpExecution = allowMcpExecution,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok(result)),
        };

    private static WebGateway CreateGateway(string serverId)
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        return new WebGateway(() => bus, new MemorySettings(), log) { ServerId = serverId };
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (condition())
                return;
            await Task.Delay(25);
        }
        Assert.True(condition(), "Timed out waiting for the gateway state to converge.");
    }

    private sealed class GatewayFixture : IDisposable
    {
        private readonly List<ClientHandle> _clients = [];

        private GatewayFixture(
            CommandRegistry serviceRegistry,
            CommandBus serviceBus,
            WebGateway gateway)
        {
            ServiceRegistry = serviceRegistry;
            ServiceBus = serviceBus;
            Gateway = gateway;
        }

        public CommandRegistry ServiceRegistry { get; }
        public CommandBus ServiceBus { get; }
        public WebGateway Gateway { get; }

        public static GatewayFixture Start(bool withCatalog = false)
        {
            var registry = new CommandRegistry();
            var log = new NullLog();
            var bus = new CommandBus(registry, log);
            if (withCatalog)
            {
                CommandCatalogCommands.RegisterAll(
                    registry,
                    new CommandSchemaExporter(registry),
                    prompts: null!,
                    gateway: static () => null,
                    source: "framework:service");
            }
            var gateway = new WebGateway(() => bus, new MemorySettings(), log);
            bus.FrontendExecutor = gateway.RelayFrontendCommandAsync;
            var result = gateway.Start(FreePort());
            Assert.True(result.Success, result.Message);
            return new GatewayFixture(registry, bus, gateway);
        }

        public ClientHandle Connect(string frontendName, string commandName, string result)
            => Connect(frontendName, FrontendDescriptor(commandName, result));

        public ClientHandle Connect(string frontendName, CommandDescriptor descriptor)
        {
            var registry = new CommandRegistry();
            registry.Register(descriptor, "app:frontend");
            var localBus = new CommandBus(registry, new NullLog());
            var client = new ShellServiceClient(
                new Uri($"http://127.0.0.1:{Gateway.Port}/"), frontendName);
            var cancellation = new CancellationTokenSource();
            var loop = client.RunEventLoopAsync(localBus, cancellation.Token);
            var handle = new ClientHandle(client, cancellation, loop);
            _clients.Add(handle);
            return handle;
        }

        public void Dispose()
        {
            foreach (var client in _clients)
                client.Stop();
            Gateway.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }

    private sealed class ClientHandle : IDisposable
    {
        private readonly ShellServiceClient _client;
        private readonly CancellationTokenSource _cancellation;
        private readonly Task _loop;
        private int _stopped;

        public ClientHandle(
            ShellServiceClient client,
            CancellationTokenSource cancellation,
            Task loop)
        {
            _client = client;
            _cancellation = cancellation;
            _loop = loop;
        }

        public void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;
            _cancellation.Cancel();
            _client.Dispose();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException ex) when (ex.InnerExceptions.All(item =>
                       item is OperationCanceledException or ObjectDisposedException))
            { }
            _cancellation.Dispose();
        }

        public void Dispose() => Stop();
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();

        public string? Get(string key)
        {
            lock (_gate)
                return _values.GetValueOrDefault(key);
        }

        public int GetInt(string key, int fallback)
            => int.TryParse(Get(key), out var value) ? value : fallback;

        public void Set(string key, string value)
        {
            lock (_gate)
                _values[key] = value;
        }

        public IReadOnlyList<KeyValuePair<string, string>> All()
        {
            lock (_gate)
                return _values.ToList();
        }
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
