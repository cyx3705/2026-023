using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using HistoryVulcan.Services;
using System.Collections.Concurrent;
using System.IO;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class CoreFreezeContractTests
{
    [Theory]
    [InlineData("")]
    [InlineData("with spaces")]
    [InlineData("quote \" and slash \\")]
    [InlineData("left=right")]
    [InlineData("中文 值")]
    public void CommandParserQuoteArgRoundTripsNamedValues(string value)
    {
        var parsed = CommandParser.Parse($"sample.run value={CommandParser.QuoteArg(value)}");

        Assert.Equal("sample.run", parsed.Name);
        Assert.Equal(value, parsed.Named["value"]);
    }

    [Fact]
    public void CommandParserRejectsDuplicateArgumentsAndUnclosedQuotes()
    {
        Assert.Throws<CommandSyntaxException>(() => CommandParser.Parse("sample.run value=1 value=2"));
        Assert.Throws<CommandSyntaxException>(() => CommandParser.Parse("sample.run value=\"open"));
    }

    [Fact]
    public async Task CommandBusBindsTypesAndEnforcesConfirmationGate()
    {
        var registry = new CommandRegistry();
        var executed = 0;
        registry.Register(new CommandDescriptor
        {
            Name = "sample.change",
            Summary = "change",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "count",
                    Description = "count",
                    Type = ParamType.Int,
                    Required = true,
                    Position = 0,
                },
                new ParameterSpec
                {
                    Name = "enabled",
                    Description = "enabled",
                    Type = ParamType.Bool,
                    Required = true,
                },
            ],
            ConfirmPrompt = context => $"change {context.GetInt("count")}",
            Handler = CommandDescriptor.Sync(context =>
            {
                executed++;
                return CommandResult.Ok($"{context.GetInt("count")}:{context.GetBool("enabled")}");
            }),
        });
        var confirmation = new ToggleConfirmation();
        var bus = new CommandBus(registry, new NullLog()) { Confirmation = confirmation };

        var invalid = await bus.ExecuteAsync("sample.change bad enabled=true", "Test");
        var rejected = await bus.ExecuteAsync("sample.change 4 enabled=true", "Test");
        Assert.Equal(0, executed);
        confirmation.Approve = true;
        var accepted = await bus.ExecuteAsync("sample.change 4 enabled=true", "Test");

        Assert.False(invalid.Success);
        Assert.Contains("整数", invalid.Message, StringComparison.Ordinal);
        Assert.False(rejected.Success);
        Assert.True(accepted.Success, accepted.Message);
        Assert.Equal("4:True", accepted.Message);
        Assert.Equal(1, executed);
    }

    [Fact]
    public async Task CommandBusAddsCommandDomainToResultsAndProgressWithoutCrossingConcurrentRuns()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "alpha.work",
            Summary = "alpha",
            Handler = async context =>
            {
                context.Progress?.Report("alpha-step");
                await Task.Delay(25);
                return CommandResult.Ok("alpha-done");
            },
        });
        registry.Register(new CommandDescriptor
        {
            Name = "beta.work",
            Summary = "beta",
            Handler = async context =>
            {
                context.Progress?.Report("beta-step");
                await Task.Delay(5);
                return CommandResult.Ok("beta-done");
            },
        });
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        await Task.WhenAll(
            bus.ExecuteAsync("alpha.work", "Test"),
            bus.ExecuteAsync("beta.work", "Test"));
        Assert.True(SpinWait.SpinUntil(
            () => log.Entries.Count(entry => entry.Category.StartsWith(
                CommandBus.ProgressCategory, StringComparison.Ordinal)) == 2,
            TimeSpan.FromSeconds(2)));

        // 类别格式是 cmd:<阶段>:<域>:<类>。这里两条夹具都是两段名，
        // DEC-025 起判为无类直接方法，故类段为空——域段仍然区分两个并发运行。
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:progress:alpha:" && entry.Message == "alpha-step");
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:progress:beta:" && entry.Message == "beta-step");
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:result:alpha:" && entry.Message.Contains("alpha-done", StringComparison.Ordinal));
        Assert.Contains(log.Entries, entry =>
            entry.Category == "cmd:result:beta:" && entry.Message.Contains("beta-done", StringComparison.Ordinal));
        Assert.Equal("cmd:result", CommandBus.ResultCategory);
        Assert.Equal("cmd:progress", CommandBus.ProgressCategory);
    }

    [Fact]
    public async Task CommandBusUsesCoreForRootCommandsAndParsedPrefixForUnknownCommands()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "ping",
            Summary = "ping",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
        });
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        Assert.True((await bus.ExecuteAsync("ping", "Test")).Success);
        Assert.False((await bus.ExecuteAsync("missing.run", "Test")).Success);

        Assert.Contains(log.Entries, entry => entry.Category == "cmd:result:core:core");
        Assert.Contains(log.Entries, entry => entry.Category == "cmd:result:missing:core");
        Assert.Equal(2, log.Entries.Count(entry => entry.Category == "cmd:Test"));
    }

    [Fact]
    public void CommandRegistryResolvesExplicitAndModuleOwnedTaxonomy()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "window.inspect",
            Domain = "vulcan",
            CommandClass = "WIN",
            Summary = "inspect",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });
        registry.Register(new CommandDescriptor
        {
            Name = "fixture.run",
            Domain = "spoofed",
            Summary = "run",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        }, "module:FixtureModule");

        Assert.Equal("vulcan", registry.GetDomain("window.inspect"));
        Assert.Equal("win", registry.GetCommandClass("window.inspect"));
        // DEC-023:模块域由 owner 强制并去掉 History 品牌前缀；描述符自填的 Domain 被覆盖。
        Assert.Equal("fixturemodule", registry.GetDomain("fixture.run"));
        // DEC-025：两段名是该域的无类直接方法，类为空串；首段是域，不再被当作类。
        Assert.Equal(string.Empty, registry.GetCommandClass("fixture.run"));
    }



    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "HistoryVulcan.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ToggleConfirmation : IConfirmationService
    {
        public bool Approve { get; set; }
        public bool Confirm(string prompt) => Approve;
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private sealed class RecordingLog : IShellLog
    {
        private readonly ConcurrentQueue<ShellLogEntry> _entries = new();

        public IReadOnlyList<ShellLogEntry> Entries => _entries.ToArray();

        public void Log(ShellLogLevel level, string category, string message)
            => _entries.Enqueue(new ShellLogEntry(DateTime.UtcNow, level, category, message));

        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }

        public IReadOnlyList<ShellLogEntry> Snapshot() => Entries;
    }
}
