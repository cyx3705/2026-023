using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// `Validate` 与 `ExecuteAsync` 对「什么是有效指令」必须同口径（4.0.0，REQ-A6）。
///
/// 这条回归来自一次真实施工：把 `vulcan.command.help` 从前端搬到服务侧后，35 个测试转红。
/// `ExecuteAsync` 配了 `RemoteExecutor` 就会把本地查不到的指令中继出去，而 `Validate`
/// 只查本地注册表并报「未知指令」——于是引用该命令的菜单在**构建期**抛异常，
/// 尽管点下去其实能正常执行。
///
/// B 阶段要把 37 条命令搬去 Aurora，这个不一致会成片触发，因此必须有测试守住。
/// </summary>
public sealed class CommandBusRemoteValidationTests
{
    [Fact]
    public void UnknownCommandIsRejectedWhenThereIsNoRemote()
    {
        var bus = NewBus();

        var error = bus.Validate("vulcan.not.registered");

        // 嵌入模式下本地注册表就是权威，笔误必须当场暴露。
        Assert.NotNull(error);
        Assert.Contains("未知指令", error);
    }

    [Fact]
    public void UnknownCommandIsAcceptedWhenARemoteExecutorIsConfigured()
    {
        var bus = NewBus();
        bus.RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("relayed"));

        // 权威注册表在服务进程，本进程无从判定——不得报「未知指令」。
        Assert.Null(bus.Validate("vulcan.not.registered"));
    }

    /// <summary>放宽只针对"本地查不到"，语法错误在任何模式下都必须报出。</summary>
    [Fact]
    public void SyntaxErrorsAreStillReportedWithARemoteExecutor()
    {
        var bus = NewBus();
        bus.RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("relayed"));

        var error = bus.Validate("vulcan.log.level \"unterminated");

        Assert.NotNull(error);
        Assert.Contains("语法错误", error);
    }

    /// <summary>本地已注册的指令仍走参数绑定校验，不因为配了远端就跳过。</summary>
    [Fact]
    public void LocallyRegisteredCommandsStillBindTheirArguments()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "local.needsvalue",
            Summary = "needs value",
            Parameters = [new ParameterSpec { Name = "count", Description = "n", Type = ParamType.Int }],
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
        });
        var bus = new CommandBus(registry, new NullLog())
        {
            RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("relayed")),
        };

        Assert.NotNull(bus.Validate("local.needsvalue count=abc"));
        Assert.Null(bus.Validate("local.needsvalue count=3"));
    }

    /// <summary>
    /// DEC-064：交给远端的命令由远端总线记回显与结果，本地不记——界面与宿主共用一份控制台日志后，
    /// 两边各记一遍就是每条命令出现两次。本地执行的命令照旧回显。
    /// </summary>
    [Fact]
    public async Task RemoteRoutedCommandsLeaveEchoAndResultToTheRemoteBus()
    {
        var registry = new CommandRegistry();
        registry.Register(new CommandDescriptor
        {
            Name = "local.ping",
            Summary = "ping",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("pong")),
        });
        var log = new RecordingLog();
        var executed = new List<string>();
        var bus = new CommandBus(registry, log)
        {
            RemoteExecutor = (_, _, _) => Task.FromResult(CommandResult.Ok("relayed")),
            ShouldUseRemoteCommand = (text, _) => !text.StartsWith("local.", StringComparison.Ordinal),
        };
        bus.Executed += (text, _, _) => executed.Add(text);

        var relayed = await bus.ExecuteAsync("remote.work", "UI");
        Assert.Equal("relayed", relayed.Message);
        Assert.Empty(log.Entries);
        Assert.Contains("remote.work", Assert.Single(executed), StringComparison.Ordinal);

        var local = await bus.ExecuteAsync("local.ping", "UI");
        Assert.True(local.Success, local.Message);
        Assert.Contains(log.Entries, entry => entry.Category == CommandBus.EchoCategoryPrefix + "UI");
        Assert.Contains(
            log.Entries,
            entry => entry.Category.StartsWith(CommandBus.ResultCategory + ":local", StringComparison.Ordinal));
    }

    private static CommandBus NewBus() => new(new CommandRegistry(), new NullLog());

}
