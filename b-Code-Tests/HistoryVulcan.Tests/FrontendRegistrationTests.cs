using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>DEC-063：宿主总线封口与唯一前端登记。</summary>
public sealed class FrontendRegistrationTests
{
    [Fact]
    public void SealedHostBusRejectsKnobWritesWhileStandaloneBusesStayConfigurable()
    {
        var standalone = new CommandBus(new CommandRegistry(), new NullLog())
        {
            Confirmation = new FixedConfirmation(true),
            UiContext = new CountingContext(),
            RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok()),
            ShouldUseRemoteCommand = (_, _) => false,
        };
        Assert.NotNull(standalone.Confirmation);
        Assert.NotNull(standalone.RemoteExecutor);

        var host = new CommandBus(new CommandRegistry(), new NullLog());
        host.SealHostWiring();
        Assert.Throws<InvalidOperationException>(() => host.Confirmation = new FixedConfirmation(true));
        Assert.Throws<InvalidOperationException>(() => host.UiContext = new CountingContext());
        Assert.Throws<InvalidOperationException>(() => host.RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok()));
        Assert.Throws<InvalidOperationException>(() => host.ShouldUseRemoteCommand = (_, _) => true);

        // 宿主自身的装配口不受封口限制。
        host.SetHostConfirmation(new FixedConfirmation(false));
        Assert.NotNull(host.Confirmation);
    }

    [Fact]
    public void OnlyOneOwnerMayHoldTheFrontend()
    {
        var bus = new CommandBus(new CommandRegistry(), new NullLog());
        var first = bus.ClaimFrontend("HistoryAurora", new FakeFrontend(confirm: true));

        var refused = Assert.Throws<InvalidOperationException>(
            () => bus.ClaimFrontend("HistoryOther", new FakeFrontend(confirm: true)));
        Assert.Contains("HistoryAurora", refused.Message, StringComparison.Ordinal);

        var replacement = new FakeFrontend(confirm: false);
        var second = bus.ClaimFrontend("historyaurora", replacement);
        Assert.Same(replacement, bus.Frontend);

        // 同 owner 的旧句柄释放不得撤掉后继登记：热重载时旧实例的 Dispose 晚于新实例的 Attach。
        first.Dispose();
        Assert.Same(replacement, bus.Frontend);

        second.Dispose();
        second.Dispose();
        Assert.Null(bus.Frontend);

        using var other = bus.ClaimFrontend("HistoryOther", new FakeFrontend(confirm: true));
        Assert.NotNull(bus.Frontend);
    }

    [Fact]
    public void ReleaseByOwnerOnlyRemovesThatOwnersFrontend()
    {
        var bus = new CommandBus(new CommandRegistry(), new NullLog());
        using var registration = bus.ClaimFrontend("HistoryAurora", new FakeFrontend(confirm: true));

        bus.ReleaseFrontend("HistoryJanus");
        Assert.NotNull(bus.Frontend);

        bus.ReleaseFrontend("HISTORYAURORA");
        Assert.Null(bus.Frontend);
    }

    [Fact]
    public async Task FrontendAnswersConfirmationAndReleaseFallsBackToHostDeny()
    {
        var registry = new CommandRegistry();
        var executed = 0;
        registry.Register(new CommandDescriptor
        {
            Name = "sample.drop",
            Summary = "drop",
            Level = CommandLevel.Ask,
            Handler = CommandDescriptor.Sync(_ =>
            {
                executed++;
                return CommandResult.Ok();
            }),
        });
        var bus = new CommandBus(registry, new NullLog());
        bus.SetHostConfirmation(new FixedConfirmation(false));
        bus.SealHostWiring();

        var frontend = new FakeFrontend(confirm: true);
        using (bus.ClaimFrontend("HistoryAurora", frontend))
        {
            var accepted = await bus.ExecuteAsync("sample.drop", "Test");
            Assert.True(accepted.Success, accepted.Message);
            Assert.Equal("确认执行 sample.drop？", Assert.Single(frontend.Prompts));
            Assert.True(bus.RequestConfirmation("继续？"));
        }

        Assert.False((await bus.ExecuteAsync("sample.drop", "Test")).Success);
        Assert.False(bus.RequestConfirmation("继续？"));
        Assert.Equal(1, executed);
    }

    [Fact]
    public void RequestConfirmationWithoutAnyChannelIsRefused()
    {
        var bus = new CommandBus(new CommandRegistry(), new NullLog());

        Assert.False(bus.RequestConfirmation("继续？"));
        Assert.Throws<ArgumentException>(() => bus.RequestConfirmation(" "));
    }

    [Fact]
    public async Task UiThreadCommandsRunOnTheRegisteredFrontendContext()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "sample.pane",
            Summary = "pane",
            RequiresUiThread = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        });
        var bus = new CommandBus(registry, new NullLog());
        var hostContext = new CountingContext();
        bus.SetHostUiContext(hostContext);
        bus.SealHostWiring();

        var frontend = new FakeFrontend(confirm: true);
        using (bus.ClaimFrontend("HistoryAurora", frontend))
        {
            Assert.Same(frontend.UiContext, bus.UiContext);
            var result = await bus.ExecuteAsync("sample.pane", "Test");
            Assert.True(result.Success, result.Message);
            Assert.Equal(1, frontend.Context.Posts);
            Assert.Equal(0, hostContext.Posts);
        }

        Assert.Same(hostContext, bus.UiContext);
    }

    [Fact]
    public void FrontendWithoutUiContextIsRejected()
    {
        var bus = new CommandBus(new CommandRegistry(), new NullLog());

        Assert.Throws<ArgumentNullException>(() => bus.ClaimFrontend("HistoryAurora", new NullContextFrontend()));
        Assert.Null(bus.Frontend);
    }

    private sealed class FakeFrontend(bool confirm) : IFrontend
    {
        public List<string> Prompts { get; } = [];

        public CountingContext Context { get; } = new();

        public SynchronizationContext UiContext => Context;

        public bool Confirm(string prompt)
        {
            Prompts.Add(prompt);
            return confirm;
        }

        public Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation)
            => Task.FromResult(CommandResult.Ok(commandName));
    }

    private sealed class NullContextFrontend : IFrontend
    {
        public SynchronizationContext UiContext => null!;

        public bool Confirm(string prompt) => true;

        public Task<CommandResult> ExecuteAsync(string commandName, string source, CancellationToken cancellation)
            => Task.FromResult(CommandResult.Ok());
    }

    private sealed class FixedConfirmation(bool answer) : IConfirmationService
    {
        public bool Confirm(string prompt) => answer;
    }

    private sealed class CountingContext : SynchronizationContext
    {
        private int _posts;

        public int Posts => Volatile.Read(ref _posts);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _posts);
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
