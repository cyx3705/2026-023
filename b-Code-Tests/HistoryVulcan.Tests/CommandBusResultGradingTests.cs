using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 结果回显的分级合同（5.6.0，REQ-HOST-006）。
///
/// 级别一直是**宿主**定的：<see cref="CommandResult"/> 没有级别这一项，模块给不出，
/// 也就无从"由模块把自己那条降级"。因此这条规则只能长在总线上，而且必须按结果的形状判，
/// 不能按域或命令名列白名单——白名单会漏掉下一个模块。
///
/// 要守住的是控制台在 Info 档位上的可读性：一句话的结果照旧可见，大段结果只留提要、
/// 正文落到 Debug，而失败无论多长都整条留在 Error。
/// </summary>
public sealed class CommandBusResultGradingTests
{
    private static (CommandBus Bus, RecordingLog Log) NewBus(string name, Func<CommandResult> handler)
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = name,
                Domain = "fixture",
                CommandClass = "grade",
                Summary = "结果分级合同测试",
                Handler = CommandDescriptor.Sync(_ => handler()),
            },
            "test");
        var log = new RecordingLog();
        return (new CommandBus(registry, log), log);
    }

    private static IEnumerable<ShellLogEntry> Results(RecordingLog log)
        => log.Entries.Where(entry =>
            entry.Category.StartsWith(CommandBus.ResultCategory, StringComparison.Ordinal));

    [Fact]
    public async Task ShortResultStaysOnInfoAsASingleEntry()
    {
        var (bus, log) = NewBus("fixture.grade.short", () => CommandResult.Ok("已切到场景 [Juno]：2 页露面"));

        await bus.ExecuteAsync("fixture.grade.short", "test");

        var entry = Assert.Single(Results(log));
        Assert.Equal(ShellLogLevel.Info, entry.Level);
        Assert.Contains("已切到场景", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiLineResultKeepsItsFirstLineOnInfoAndTheBodyOnDebug()
    {
        const string listing = "命令集: 3 / 3 条\n  alpha.one\n  alpha.two\n  alpha.three";
        var (bus, log) = NewBus("fixture.grade.listing", () => CommandResult.Ok(listing));

        await bus.ExecuteAsync("fixture.grade.listing", "test");

        var entries = Results(log).ToList();
        Assert.Equal(2, entries.Count);

        var digest = Assert.Single(entries, entry => entry.Level == ShellLogLevel.Info);
        Assert.Equal("✓ 命令集: 3 / 3 条（另有 3 行，DEBUG 级可见）", digest.Message);
        Assert.DoesNotContain("alpha.one", digest.Message, StringComparison.Ordinal);

        // 正文一个字都不能丢，只是换了级别。
        var full = Assert.Single(entries, entry => entry.Level == ShellLogLevel.Debug);
        Assert.Equal("✓ " + listing, full.Message);
    }

    [Fact]
    public async Task OverlongSingleLineResultReportsOnlyItsSizeOnInfo()
    {
        var payload = new string('x', 5000);
        var (bus, log) = NewBus("fixture.grade.payload", () => CommandResult.Ok(payload));

        await bus.ExecuteAsync("fixture.grade.payload", "test");

        var entries = Results(log).ToList();
        var digest = Assert.Single(entries, entry => entry.Level == ShellLogLevel.Info);
        Assert.Equal("✓ （5000 字，DEBUG 级可见）", digest.Message);
        Assert.Single(entries, entry => entry.Level == ShellLogLevel.Debug && entry.Message.Length > 5000);
    }

    [Fact]
    public async Task FailureIsNeverDowngradedHoweverLongItIs()
    {
        var reason = "未知指令: nope\n你是不是想输入: " + new string('y', 500);
        var (bus, log) = NewBus("fixture.grade.fail", () => CommandResult.Fail(reason));

        await bus.ExecuteAsync("fixture.grade.fail", "test");

        var entry = Assert.Single(Results(log));
        Assert.Equal(ShellLogLevel.Error, entry.Level);
        Assert.Equal("✗ " + reason, entry.Message);
    }

    [Fact]
    public async Task ShortProgressLinesStayOnInfoAndBulkOnesDropToDebug()
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "fixture.grade.progress",
                Domain = "fixture",
                CommandClass = "grade",
                Summary = "进度分级合同测试",
                Handler = CommandDescriptor.Sync(context =>
                {
                    var progress = context.Progress!;
                    progress.Report("浅克隆 main（深度 250）...");
                    progress.Report(new string('z', 1200));
                    return CommandResult.Ok("完成");
                }),
            },
            "test");
        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        await bus.ExecuteAsync("fixture.grade.progress", "test");

        // Progress<T> 把回调编组到捕获的同步上下文；测试线程上没有上下文，
        // 两条进度因此落在线程池上，指令返回时不保证都已记完。
        List<ShellLogEntry> progress = [];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            progress = log.Entries
                .Where(entry => entry.Category.StartsWith(CommandBus.ProgressCategory, StringComparison.Ordinal))
                .ToList();
            if (progress.Count >= 2)
                break;
            await Task.Delay(10);
        }

        Assert.Equal(2, progress.Count);
        Assert.Single(
            progress,
            entry => entry.Level == ShellLogLevel.Info
                     && entry.Message.StartsWith("浅克隆", StringComparison.Ordinal));
        Assert.Single(
            progress,
            entry => entry.Level == ShellLogLevel.Debug && entry.Message.Length == 1200);
    }
}
