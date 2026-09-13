using System.IO.Pipes;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class RuntimePipeServerTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task MalformedRequestDoesNotStopTheRuntimePipe()
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        registry.Register(new CommandDescriptor
        {
            Name = "vulcan.module.list",
            Summary = "test",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("alive")),
        });

        var composition = new ServiceComposition
        {
            ServiceName = "RuntimePipeTests",
            Registry = registry,
            Bus = bus,
            Settings = new MemorySettings(),
            Log = log,
        };
        var identityName = "RuntimePipeTests-" + Guid.NewGuid().ToString("N");
        using var server = RuntimePipeServer.Start(composition, identityName, "test");

        using (var malformed = await ConnectAsync(identityName))
        {
            await malformed.Writer.WriteLineAsync("{not-json");
            var response = await ReadAsync<RuntimeResponse>(malformed.Reader);
            Assert.NotNull(response);
            Assert.False(response!.Success);
            Assert.Contains("JSON", response.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var valid = await ConnectAsync(identityName);
        var hello = valid.Hello;
        var request = new RuntimeRequest(
            RuntimePipeProtocol.RequestType,
            hello.Challenge,
            Guid.NewGuid().ToString("N"),
            "vulcan.module.list",
            false,
            hello.ProcessId,
            hello.StartedUtc,
            hello.InstanceId,
            hello.ModuleInstanceIds);
        await valid.Writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));

        var validResponse = await ReadAsync<RuntimeResponse>(valid.Reader);
        Assert.NotNull(validResponse);
        Assert.True(validResponse!.Success, validResponse.Message);
        Assert.Equal("alive", validResponse.Message);
    }

    [Fact]
    public async Task DisposeCancelsAnIdleRuntimeClient()
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var bus = new CommandBus(registry, log);
        var composition = new ServiceComposition
        {
            ServiceName = "RuntimePipeTests",
            Registry = registry,
            Bus = bus,
            Settings = new MemorySettings(),
            Log = log,
        };
        var identityName = "RuntimePipeTests-" + Guid.NewGuid().ToString("N");
        var server = RuntimePipeServer.Start(composition, identityName, "test");
        using var client = await ConnectAsync(identityName);

        var dispose = Task.Run(server.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        server.Dispose();
    }

    [Theory]
    [InlineData("portunus.mcp.status")]
    [InlineData("portunus.mcp.start")]
    [InlineData("portunus.mcp.stop")]
    public async Task ModuleSpecificCommandsAreRejectedEvenWhenRegisteredAndApproved(string command)
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var executed = false;
        registry.Register(new CommandDescriptor
        {
            Name = command,
            Summary = "test",
            Handler = CommandDescriptor.Sync(_ => { executed = true; return CommandResult.Ok("unexpected"); }),
        });
        var composition = new ServiceComposition
        {
            ServiceName = "RuntimePipeTests",
            Registry = registry,
            Bus = new CommandBus(registry, log),
            Settings = new MemorySettings(),
            Log = log,
        };
        Assert.Equal(
            new[] { "vulcan.module.install", "vulcan.module.list", "vulcan.module.ready", "vulcan.module.reload" },
            RuntimePipeServer.AllowedCommands.OrderBy(value => value, StringComparer.Ordinal));
        var identity = "RuntimePipeTests-" + Guid.NewGuid().ToString("N");
        using var server = RuntimePipeServer.Start(composition, identity, "test");
        using var client = await ConnectAsync(identity);
        var hello = client.Hello;
        var request = new RuntimeRequest(
            RuntimePipeProtocol.RequestType, hello.Challenge, Guid.NewGuid().ToString("N"),
            command, true, hello.ProcessId, hello.StartedUtc, hello.InstanceId, hello.ModuleInstanceIds);
        await client.Writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var response = await ReadAsync<RuntimeResponse>(client.Reader);
        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.False(executed);
    }

    [Theory]
    [InlineData("vulcan.module.reload", false, true, false)]
    [InlineData("vulcan.module.install", false, true, false)]
    [InlineData("vulcan.module.reload", true, false, false)]
    [InlineData("vulcan.module.install", true, false, false)]
    [InlineData("vulcan.module.reload", true, true, true)]
    [InlineData("vulcan.module.install", true, true, true)]
    public async Task RuntimeActionsRequireBothApprovalAndCurrentHandshake(
        string command, bool approve, bool validHandshake, bool expected)
    {
        var registry = new CommandRegistry();
        var log = new NullLog();
        var executed = false;
        registry.Register(new CommandDescriptor
        {
            Name = command,
            Summary = "test",
            Handler = CommandDescriptor.Sync(_ => { executed = true; return CommandResult.Ok("accepted"); }),
        });
        var composition = new ServiceComposition
        {
            ServiceName = "RuntimePipeTests",
            Registry = registry,
            Bus = new CommandBus(registry, log),
            Settings = new MemorySettings(),
            Log = log,
        };
        var identity = "RuntimePipeTests-" + Guid.NewGuid().ToString("N");
        using var server = RuntimePipeServer.Start(composition, identity, "test");
        using var client = await ConnectAsync(identity);
        var hello = client.Hello;
        var request = new RuntimeRequest(
            RuntimePipeProtocol.RequestType, validHandshake ? hello.Challenge : "stale",
            Guid.NewGuid().ToString("N"), command, approve, hello.ProcessId,
            hello.StartedUtc, hello.InstanceId, hello.ModuleInstanceIds);
        await client.Writer.WriteLineAsync(JsonSerializer.Serialize(request, JsonOptions));
        var response = await ReadAsync<RuntimeResponse>(client.Reader);
        Assert.NotNull(response);
        Assert.Equal(expected, response.Success);
        Assert.Equal(expected, executed);
    }

    private static async Task<PipeConnection> ConnectAsync(string identityName)
    {
        var client = new NamedPipeClientStream(
            ".", RuntimePipeProtocol.PipeName(identityName), PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(2000);
        var reader = new StreamReader(client, leaveOpen: true);
        var writer = new StreamWriter(client, leaveOpen: true) { AutoFlush = true };
        var hello = await ReadAsync<RuntimeHello>(reader);
        Assert.NotNull(hello);
        return new PipeConnection(client, reader, writer, hello!);
    }

    private static async Task<T?> ReadAsync<T>(StreamReader reader)
    {
        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2));
        return string.IsNullOrWhiteSpace(line)
            ? default
            : JsonSerializer.Deserialize<T>(line, JsonOptions);
    }

    private sealed class PipeConnection(
        NamedPipeClientStream client,
        StreamReader reader,
        StreamWriter writer,
        RuntimeHello hello) : IDisposable
    {
        public StreamReader Reader { get; } = reader;
        public StreamWriter Writer { get; } = writer;
        public RuntimeHello Hello { get; } = hello;

        public void Dispose()
        {
            Writer.Dispose();
            Reader.Dispose();
            client.Dispose();
        }
    }

}
