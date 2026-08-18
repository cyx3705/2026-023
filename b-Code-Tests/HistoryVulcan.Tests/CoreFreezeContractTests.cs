using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Mcp;
using HistoryVulcan.Extensibility.Mcp;
using HistoryVulcan.Services;
using HistoryVulcan.Services.Mcp;
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



    [Fact]
    public async Task PromptLookupsDegradeForCommandsWithoutALocalProjection()
    {
        // 命令集详情页对每条选中指令都会拉一次 vulcan.prompt.get / history。
        // 提示词投影建立在本进程注册表上，而模块指令注册在服务进程，
        // 于是点一条 janus.* 就抛 InvalidOperationException——实测累计 156 条红字。
        // 只读查询必须如实回答「没有本地投影」，不能当成错误。
        var root = Path.Combine(Path.GetTempPath(), $"HistoryVulcan-prompt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "vulcan.command.list",
                Domain = "vulcan",
                CommandClass = "command",
                Summary = "本进程注册的指令",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });

            var log = new NullLog();
            var store = new PromptGovernanceStore(root, log);
            var exporter = new CommandSchemaExporter(registry);
            PromptGovernanceCommands.RegisterAll(registry, exporter, store);
            var bus = new CommandBus(registry, log);

            // 本进程有投影：正常返回状态。
            var local = await bus.ExecuteAsync("vulcan.prompt.get name=vulcan.command.list", "test");
            Assert.True(local.Success, local.Message);

            // 模块指令没有本地投影：成功返回并说明原因，不得失败、不得抛异常。
            var moduleGet = await bus.ExecuteAsync("vulcan.prompt.get name=janus.proj.list", "test");
            Assert.True(moduleGet.Success, moduleGet.Message);
            Assert.Contains("没有本地 MCP 投影", moduleGet.Message, StringComparison.Ordinal);

            var moduleHistory = await bus.ExecuteAsync("vulcan.prompt.history name=janus.proj.list", "test");
            Assert.True(moduleHistory.Success, moduleHistory.Message);
            Assert.Contains("没有本地 MCP 投影", moduleHistory.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PromptCommandsReturnSafeIntegrityValidationErrors()
    {
        var root = TemporaryDirectory();
        try
        {
            var registry = new CommandRegistry();
            registry.Register(new CommandDescriptor
            {
                Name = "sample.run",
                Summary = "sample",
                Readonly = true,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
            });
            var store = new PromptGovernanceStore(root, new NullLog());
            var exporter = new CommandSchemaExporter(registry)
            {
                DescriptionsProvider = store.AllEffectiveDescriptions,
            };
            PromptGovernanceCommands.RegisterAll(registry, exporter, store);
            var bus = new CommandBus(registry, new NullLog());

            var proposal = await bus.ExecuteAsync(
                "vulcan.prompt.propose name=sample.run text=???????? reason=encoding", "MCP:test");
            var direct = await bus.ExecuteAsync(
                "vulcan.mcp.desc name=sample.run text=???????? reason=encoding", "UI");

            Assert.False(proposal.Success);
            Assert.Contains("编码损坏", proposal.Message, StringComparison.Ordinal);
            Assert.False(direct.Success);
            Assert.Contains("编码损坏", direct.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PromptGovernanceProposalApprovalApplyAndRevertAreStateful()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = new PromptGovernanceStore(root, new NullLog());
            var original = store.ApplyDirect(
                "sample.run", "original description", "test", "seed");
            var proposal = store.CreateProposal(
                "sample.run", "original description", "improved description",
                "clarify", "review", "test-client");

            Assert.Throws<InvalidOperationException>(() => store.ApplyProposal(proposal.Id, "reviewer"));
            var approved = store.ApproveProposal(proposal.Id, "reviewer");
            var applied = store.ApplyProposal(approved.Id, "reviewer");
            var reverted = store.RevertToRevision(original.Id, "reviewer", "rollback");

            Assert.Equal("approved", approved.Status);
            Assert.Equal("improved description", applied.Description);
            Assert.Equal("original description", reverted.Description);
            Assert.Equal(original.Id, reverted.RevertedFrom);
            Assert.Equal(reverted.Id, store.GetCurrentRevision("sample.run")!.Id);
            var reloaded = new PromptGovernanceStore(root, new NullLog());
            Assert.Equal("original description", reloaded.GetCurrentRevision("sample.run")!.Description);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
