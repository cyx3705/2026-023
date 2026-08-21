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
    /// 命令行暴露**缺省全关**，只有显式声明的指令可执行。
    /// </summary>
    /// <remarks>
    /// 与 MCP 那一面相反是刻意的：MCP 面对一份长年累月长起来的存量指令集，
    /// 缺省全关等于让它一夜失能，所以它按只读/危险/隐藏推断；
    /// CLI 是新面，没有存量要照顾，缺省全开等于把「还没想过要不要暴露」写成「已经暴露」。
    /// </remarks>
    [Fact]
    public void CliExposureIsClosedUnlessDeclared()
    {
        // 只读且无害的指令**照样**不在 CLI 上，除非显式声明——这正是缺省全关的意思。
        var plain = new CommandDescriptor
        {
            Name = "sample.run",
            Summary = "sample",
            Readonly = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };
        var declared = new CommandDescriptor
        {
            Name = "sample.run",
            Summary = "sample",
            Readonly = true,
            AllowCliExecution = true,
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

        Assert.False(CliExposurePolicy.IsExposed(plain));
        Assert.True(CliExposurePolicy.IsExposed(declared));

        // 拒绝说明必须写清「不是这条指令不存在」，否则使用者会以为自己打错了名字，
        // 转而去猜别的写法——而真正该做的是给那条指令加声明。
        Assert.Contains("未声明", CliExposurePolicy.RefusalReason(plain.Name), StringComparison.Ordinal);
        Assert.Contains(nameof(CommandDescriptor.AllowCliExecution),
            CliExposurePolicy.RefusalReason(plain.Name), StringComparison.Ordinal);
    }

    /// <summary>
    /// 已声明的那几条必须**恰好**是开发管线要用的，多一条都要有人点头。
    /// </summary>
    /// <remarks>
    /// 按源码文本扫描而不是构造一份组合根：<c>ServiceComposer.Build</c> 会创建真实的
    /// 数据目录、日志与设置，测试不该去动使用者的 <c>%AppData%</c>。
    /// 而且真正要守的事情发生在**写下声明的那一刻**——扫描源码正好守在那里，
    /// 与那条指令在某份组合里可达与否无关。
    ///
    /// 用集合断言而不是计数：数字变了只说明「多了一条」，集合断言直接说出多的是哪条。
    /// 本轮 MCP 迁移里正是集合断言指出了改名绕过硬排除。
    ///
    /// 只覆盖宿主自己注册的。模块可以自行声明，那属于模块的暴露面，由模块的门禁去守。
    ///
    /// 开发路线（worktree / release）整条都在清单里，那正是第 6、7 步合起来的意义：
    /// 走 MCP 要 agent 会话活着，走 Web 要 Portunus 装载成功，而需要修模块的时刻
    /// 恰恰是这些前提不成立的时刻。
    /// </remarks>
    [Fact]
    public void OnlyTheDevelopmentPipelineCommandsAreDeclaredForTheCli()
    {
        string[] sources =
        [
            Path.Combine("b-Code-HistoryVulcan", "src", "HistoryVulcan.ServiceHost", "ServiceComposer.cs"),
            Path.Combine("b-Code-HistoryVulcan", "src", "HistoryVulcan.Services", "Commands", "CommandCatalogCommands.cs"),
            Path.Combine("b-Code-HistoryVulcan", "src", "HistoryVulcan.Services", "Development", "WorktreeCommands.cs"),
            Path.Combine("b-Code-HistoryVulcan", "src", "HistoryVulcan.Services", "Development", "ReleaseCommands.cs"),
        ];

        var declared = new List<string>();
        foreach (var relative in sources)
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryRoot(), relative));
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains("AllowCliExecution = true", StringComparison.Ordinal))
                    continue;

                // 声明紧跟在 Name = "..." 之后，见两处注册点。
                var name = System.Text.RegularExpressions.Regex.Match(
                    lines[index - 1], "Name = \"(?<name>[^\"]+)\"");
                Assert.True(name.Success, $"{relative}:{index + 1} 的声明上一行不是指令名");
                declared.Add(name.Groups["name"].Value);
            }
        }

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
            declared.Order(StringComparer.OrdinalIgnoreCase));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryVulcan 仓库根目录");
    }
}
