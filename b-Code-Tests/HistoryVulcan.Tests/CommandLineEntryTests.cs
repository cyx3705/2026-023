using HistoryVulcan.Core.Commands;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 命令行入口的三条边界：参数判定、暴露声明、离线装包。
/// </summary>
public sealed class CommandLineEntryTests
{
    /// <summary>
    /// 不认识的参数必须报错，**绝不能落到「起服务」那条缺省路径上**。
    /// </summary>
    /// <remarks>
    /// 这是备忘录点名的地雷：此前 Main 静默忽略未知参数，
    /// <c>HistoryVulcan.exe --instal-module pkg</c>（少一个 l）会默默起第二个宿主，
    /// 而使用者以为自己装了个包——两个宿主抢同一个端口，症状出现在几分钟之后、别的地方。
    ///
    /// 判定被抽成纯函数正是为了这条能被测到：拉起真实进程来验「不会起服务」，
    /// 万一守卫失效，测试本身就会在机器上留下一个野宿主。
    /// </remarks>
    [Theory]
    [InlineData("--instal-module", "pkg")]      // 少一个 l
    [InlineData("--Cli")]                        // 大小写对，但缺指令文本
    [InlineData("--help")]
    [InlineData("worktree", "create")]           // 误当成子命令
    public void UnrecognizedArgumentsNeverFallThroughToStartingTheService(params string[] args)
    {
        var parsed = HostArgumentParser.Parse(args);

        Assert.Equal(HostAction.Error, parsed.Action);
        Assert.NotEmpty(parsed.Error);
    }

    [Fact]
    public void NoArgumentsStartsTheServiceAndTheLegacySwitchIsStillAccepted()
    {
        Assert.Equal(HostAction.RunService, HostArgumentParser.Parse([]).Action);

        // 自启动项、既有快捷方式与 vulcan.svc.restart 都还带着 --service。
        // 对它报错等于让升级过程平白失败。
        Assert.Equal(
            HostAction.RunService,
            HostArgumentParser.Parse([HostArgumentParser.LegacyServiceSwitch]).Action);
    }

    [Fact]
    public void ValueSwitchesReportAMissingValueInsteadOfDegradingToService()
    {
        foreach (var name in new[]
                 {
                     HostArgumentParser.ExportManualSwitch,
                     HostArgumentParser.InstallModuleSwitch,
                     HostArgumentParser.CommandLineSwitch,
                 })
        {
            var parsed = HostArgumentParser.Parse([name]);
            Assert.Equal(HostAction.Error, parsed.Action);
            Assert.Contains(name, parsed.Error, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>--cli</c> 之后的全部参数并成一条指令文本。
    /// </summary>
    /// <remarks>
    /// 指令带参数是常态（<c>--cli vulcan.module.install path=D:\pkg</c>）。
    /// 若只取紧随其后的一个 token，使用者就得在 shell 里给整条指令加引号——
    /// 而 Windows 的引号转义正是这条路上最容易出错的地方。
    /// </remarks>
    [Fact]
    public void CommandTextTakesEverythingAfterTheSwitch()
    {
        var parsed = HostArgumentParser.Parse(
            [HostArgumentParser.CommandLineSwitch, "vulcan.module.install", "path=D:\\pkg"]);

        Assert.Equal(HostAction.RunCommand, parsed.Action);
        Assert.Equal("vulcan.module.install path=D:\\pkg", parsed.Value);
    }

    /// <summary>
    /// 命令行面**只认名单**，名单外一律拒绝。
    /// </summary>
    /// <remarks>
    /// 与别的消费面相反是刻意的：暴露与否，描述符上只有一个声明
    /// （<c>HiddenReason</c>），三个面共用；而命令行有一条别的面没有的额外要求——
    /// <c>vulcan.module.install</c> / <c>remove</c> 对 MCP 是硬排除（不能让远端给自己
    /// 换宿主的包），对命令行却是必须有（模块坏掉时它是唯一还能换包的路）。
    /// 一个布尔位给不出「这个面要、那个面不要」这个答案，所以收窄留在命令行自己这里。
    /// </remarks>
    [Fact]
    public void TheCliSurfaceIsExactlyItsAllowList()
    {
        Assert.True(CliExposurePolicy.IsExposed("vulcan.module.install"));
        Assert.True(CliExposurePolicy.IsExposed("VULCAN.MODULE.INSTALL"));

        // 只读且无害的指令**照样**不在命令行上——命令行不按危险性推断，只认名单。
        Assert.False(CliExposurePolicy.IsExposed("vulcan.command.help"));
        Assert.False(CliExposurePolicy.IsExposed(""));

        // 拒绝说明必须写清「不是这条指令不存在」，否则使用者会以为自己打错了名字，
        // 转而去猜别的写法——而真正该做的是判断它该不该进这份名单。
        var reason = CliExposurePolicy.RefusalReason("vulcan.command.help");
        Assert.Contains("不在命令行面上", reason, StringComparison.Ordinal);
        Assert.Contains(nameof(CliExposurePolicy.ExposedCommands), reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// 名单与真实注册表的对账：名单里的指令必须都还在。
    /// </summary>
    /// <remarks>
    /// 名单按名字写，指令改名时会**静默失配**，而失配的方向最坏：等到模块坏掉、
    /// 需要 --cli 救火时才发现恢复指令不在面上。本体系因为按名字写的规则栽过四次，
    /// 所以命令行入口每次执行前都做这次对账。这里验对账本身能报出缺失。
    /// </remarks>
    [Fact]
    public void TheAllowListIsReconciledAgainstTheRegistry()
    {
        var registry = new CommandRegistry();
        foreach (var name in CliExposurePolicy.ExposedCommands)
        {
            registry.Register(new CommandDescriptor
            {
                Name = name,
                Summary = name,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });
        }

        Assert.Empty(CliExposurePolicy.MissingCommands(registry));

        // 模拟一次改名：注册表里少了一条，对账必须点名说出是哪条。
        var renamed = new CommandRegistry();
        foreach (var name in CliExposurePolicy.ExposedCommands.Skip(1))
        {
            renamed.Register(new CommandDescriptor
            {
                Name = name,
                Summary = name,
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            });
        }

        Assert.Equal(
            [CliExposurePolicy.ExposedCommands[0]],
            CliExposurePolicy.MissingCommands(renamed));
    }

    /// <summary>
    /// 名单必须**恰好**是开发管线与模块恢复那几组，多一条都要有人点头。
    /// </summary>
    /// <remarks>
    /// 走 MCP 要 agent 会话活着，走 Web 要 Portunus 装载成功，而需要修模块的时刻
    /// 恰恰是这些前提不成立的时刻。这条理由不适用于任何别的指令，名单也就到此为止。
    /// </remarks>
    [Fact]
    public void OnlyTheDevelopmentPipelineAndRecoveryCommandsAreOnTheCli()
    {
        Assert.Equal(
            new[]
            {
                "vulcan.command.domains",
                "vulcan.command.list",
                "vulcan.command.show",
                "vulcan.module.install",
                "vulcan.module.list",
                "vulcan.module.reload",
                "vulcan.module.remove",
                "vulcan.module.unload",
                "vulcan.release.cycle",
                "vulcan.release.log",
                "vulcan.release.modules",
                "vulcan.release.status",
                "vulcan.worktree.create",
                "vulcan.worktree.list",
                "vulcan.worktree.merge",
                "vulcan.worktree.root",
            }.Order(StringComparer.OrdinalIgnoreCase),
            CliExposurePolicy.ExposedCommands.Order(StringComparer.OrdinalIgnoreCase));
    }
}
