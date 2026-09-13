using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class HostConfirmationTests
{
    [Fact]
    public void DeniesAndLeavesATrace()
    {
        var log = new RecordingLog();
        var confirmation = new DenyConfirmation(log);

        Assert.False(confirmation.Confirm("prompt-secret"));
        Assert.Contains(log.Messages, message => message.Contains("界面未装载"));
        Assert.DoesNotContain(log.Messages, message => message.Contains("prompt-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RouterUsesCurrentInstalledConfirmation()
    {
        var registry = new CommandRegistry();
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);
        var composition = new ServiceComposition
        {
            ServiceName = "test",
            Registry = registry,
            Bus = bus,
            Log = log,
            Settings = new MemorySettings(),
        };
        HistoryVulcan.ServiceHost.ServiceHost.InstallDefaultConfirmation(composition);
        registry.Register(new CommandDescriptor
        {
            Name = "test.confirm",
            Summary = "test",
            Level = CommandLevel.Ask,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("executed")),
        });
        Assert.False((await bus.ExecuteAsync("test.confirm", "UI")).Success);
        bus.Confirmation = new AcceptConfirmation();
        Assert.True((await bus.ExecuteAsync("test.confirm", "UI")).Success);
        bus.Confirmation = null;
        Assert.False((await bus.ExecuteAsync("test.confirm", "UI")).Success);
    }

    private sealed class AcceptConfirmation : IConfirmationService
    {
        public bool Confirm(string message) => true;
    }

    /// <summary>只记消息文本的日志：断言的是"拒绝时留下了可见痕迹"，与条目结构无关。</summary>

}
