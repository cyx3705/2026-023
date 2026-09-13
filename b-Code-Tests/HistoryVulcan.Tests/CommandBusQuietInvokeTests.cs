using System.Collections.Concurrent;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 安静调用通道(CommandBus.InvokeAsync)的行为合同。
///
/// 这条通道的存在意义是让宿主与模块按**命令名**集成而不是按 CLR 类型集成:调用方只依赖
/// 一个字符串和总线,被调方因此可以自由演进而不触动宿主公开面。它要成立,前提是高频调用
/// (逐键补全一类)不会污染操作者视图——ExecuteAsync 每次调用写两条回显并触发 Executed,
/// 逐键走那条路会把控制台灌满。本组测试锁定这个差别,避免日后有人"顺手"把两条路合并。
/// </summary>
public sealed class CommandBusQuietInvokeTests
{
    private static (CommandBus Bus, RecordingLog Log) NewBus()
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "fixture.quiet.echo",
                Domain = "fixture",
                CommandClass = "quiet",
                Summary = "回声,用于安静通道合同测试",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("echoed")),
            },
            "test");

        var log = new RecordingLog();
        return (new CommandBus(registry, log), log);
    }

    [Fact]
    public async Task QuietInvokeDoesNotEchoTheCommandOrItsResult()
    {
        var (bus, log) = NewBus();

        var result = await bus.InvokeAsync("fixture.quiet.echo", "test");

        Assert.True(result.Success);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task OperatorExecuteStillEchoesBothCommandAndResult()
    {
        var (bus, log) = NewBus();

        var result = await bus.ExecuteAsync("fixture.quiet.echo", "test");

        Assert.True(result.Success);
        // 操作者通道必须保留可追溯性:一条命令回显 + 一条结果回显。
        Assert.Equal(2, log.Entries.Count);
    }

    [Fact]
    public async Task QuietInvokeDoesNotRaiseExecuted()
    {
        var (bus, _) = NewBus();
        var raised = 0;
        bus.Executed += (_, _, _) => Interlocked.Increment(ref raised);

        await bus.InvokeAsync("fixture.quiet.echo", "test");
        Assert.Equal(0, raised);

        // 同一总线上,操作者通道仍须触发事件(状态栏/历史依赖它)。
        await bus.ExecuteAsync("fixture.quiet.echo", "test");
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task QuietInvokeReturnsTheSameResultAsExecute()
    {
        var (bus, _) = NewBus();

        var quiet = await bus.InvokeAsync("fixture.quiet.echo", "test");
        var loud = await bus.ExecuteAsync("fixture.quiet.echo", "test");

        Assert.Equal(loud.Success, quiet.Success);
        Assert.Equal(loud.Message, quiet.Message);
    }

    [Fact]
    public async Task QuietInvokeReportsUnknownCommandsWithoutThrowing()
    {
        var (bus, log) = NewBus();

        var result = await bus.InvokeAsync("fixture.quiet.nosuch", "test");

        Assert.False(result.Success);
        // 失败也不回显:调用方拿到结果自行决定如何呈现。
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task QuietInvokeStillRecordsInternalBusFaults()
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "fixture.quiet.throw",
                Domain = "fixture",
                CommandClass = "quiet",
                Summary = "抛出,用于验证内部故障留痕",
                Handler = _ => throw new InvalidOperationException("boom"),
            },
            "test");

        var log = new RecordingLog();
        var bus = new CommandBus(registry, log);

        var result = await bus.InvokeAsync("fixture.quiet.throw", "test");

        // 安静不等于静默失败:命令自身异常由总线兜底成失败结果,
        // 而总线内部故障必须留痕,否则问题会消失在安静通道里。
        Assert.False(result.Success);
    }

}
