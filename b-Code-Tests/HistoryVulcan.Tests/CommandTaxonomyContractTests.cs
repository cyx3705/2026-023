using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.Commands;
using HistoryVulcan.Core.Mcp;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// DEC-023 / REQ-CMD-010 / REQ-CMD-011:三段式九类分类法与模块域去品牌前缀。
/// </summary>
public sealed class CommandTaxonomyContractTests
{
    /// <summary>3.3.2 认可的九个内置类，见技术合同 REQ-CMD-010。</summary>
    private static readonly HashSet<string> BuiltinClasses = new(StringComparer.Ordinal)
    {
        "app", "command", "ui", "log", "mcp", "module", "prompt", "svc", "web",
    };

    [Theory]
    [InlineData("HistoryJanus", "janus")]
    [InlineData("HistoryMercury", "mercury")]
    [InlineData("HistoryMinerva", "minerva")]
    [InlineData("HistoryVulcan", "vulcan")]
    [InlineData("historyjanus", "janus")]
    [InlineData("HISTORYJANUS", "janus")]
    [InlineData("  HistoryJanus  ", "janus")]
    [InlineData("Fixture", "fixture")]
    [InlineData("History", "history")]
    [InlineData("", "")]
    public void ModuleDomainStripsTheHistoryBrandPrefix(string moduleName, string expected)
        => Assert.Equal(expected, ModuleDomainNaming.ToDomain(moduleName));

    [Fact]
    public void ModuleOwnedCommandsUseTheNormalizedDomainNotTheManifestName()
    {
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "janus.project.commit",
                Domain = "spoofed",
                CommandClass = "project",
                Summary = "commit",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            },
            "module:HistoryJanus");

        // owner 强制且归一化：描述符自填的 Domain 被覆盖，History 前缀被剥离。
        Assert.Equal("janus", registry.GetDomain("janus.project.commit"));
        Assert.Equal("project", registry.GetCommandClass("janus.project.commit"));
    }

    [Fact]
    public void EveryBuiltinCommandNameIsThreeSegmentLowercaseWithoutHyphen()
    {
        foreach (var name in BuiltinCommandDefinitions.Names)
        {
            var parts = name.Split('.');
            Assert.True(parts.Length == 3, $"{name} 不是三段式");
            Assert.Equal(name.ToLowerInvariant(), name);
            Assert.DoesNotContain('-', name);
            Assert.Equal("vulcan", parts[0]);
            Assert.Contains(parts[1], BuiltinClasses);
        }
    }

    [Fact]
    public void SharedBuiltinDefinitionsCarryAnApprovedClass()
    {
        foreach (var name in BuiltinCommandDefinitions.Names)
        {
            var descriptor = BuiltinCommandDefinitions.Bind(
                name,
                _ => Task.FromResult(CommandResult.Ok()));

            Assert.Equal("vulcan", descriptor.Domain);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.CommandClass), $"{name} 未声明类");
            Assert.Contains(descriptor.CommandClass!, BuiltinClasses);
        }
    }

    [Fact]
    public void RetiredClassesAndTheShadowDebugDomainAreGone()
    {
        // core / frontend / win / layout / panel 五个类与 debug 影子域在 3.3.2 退役。
        foreach (var retired in new[] { "core", "frontend", "win", "layout", "panel" })
            Assert.DoesNotContain(retired, BuiltinClasses);

        foreach (var name in BuiltinCommandDefinitions.Names)
            Assert.False(
                name.StartsWith("debug.", StringComparison.OrdinalIgnoreCase),
                $"{name} 仍在影子域 debug 下");
    }

    /// <summary>
    /// 宿主源码里声明了 <c>HiddenReason</c> 的指令，必须**恰好**是这四条。
    /// </summary>
    /// <remarks>
    /// 4.8.0 之前这里有五个测试，分别验 <c>McpExposurePolicy.HardExclusionReason</c>
    /// 对一批**字符串字面量**的返回值。那种写法验的是规则，不是现实：
    /// 规则可以完好无损，而它盯着的指令早已改名或根本不存在——
    /// 被验的 <c>vulcan.log.flood</c> 与 <c>vulcan.svc.forgetfrontend</c> 全仓没有注册点，
    /// 五个测试里有两个在为不存在的指令背书。
    ///
    /// 现在判据挂在描述符上，测试也跟着改为**扫源码**：守的是「写下声明的那一刻」，
    /// 与那条指令在某份组合里可达与否无关，也不需要构造组合根去动使用者的 %AppData%。
    ///
    /// 用集合断言而不是计数：数字变了只说明多了一条，集合断言直接说出多的是哪条。
    ///
    /// 只覆盖宿主自己注册的。模块的隐藏声明属于模块的暴露面，由模块的门禁去守
    /// （HistoryPortunus 的 6 条 <c>portunus.mcp.*</c>、HistoryAurora 的 5 条页面协议通道）。
    /// </remarks>
    [Fact]
    public void OnlyFourHostCommandsDeclareThemselvesHiddenFromRemoteClients()
    {
        string[] sources =
        [
            Path.Combine("b-Code-HistoryVulcan", "HistoryVulcan.ServiceHost", "ServiceCommands.cs"),
            Path.Combine("b-Code-HistoryVulcan", "HistoryVulcan.ServiceHost", "ServiceComposer.cs"),
            Path.Combine("b-Code-HistoryVulcan", "HistoryVulcan.Services", "Commands", "CommandCatalogCommands.cs"),
            Path.Combine("b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "WorktreeCommands.cs"),
            Path.Combine("b-Code-HistoryVulcan", "HistoryVulcan.Services", "Development", "ReleaseCommands.cs"),
        ];

        var declared = new List<string>();
        foreach (var relative in sources)
        {
            var lines = File.ReadAllLines(Path.Combine(RepositoryPaths.Root(), relative));
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains("HiddenReason = ", StringComparison.Ordinal))
                    continue;

                // 声明紧跟在 Name = "..." 之后。
                var name = System.Text.RegularExpressions.Regex.Match(
                    lines[index - 1], "Name = \"(?<name>[^\"]+)\"");
                Assert.True(name.Success, $"{relative}:{index + 1} 的声明上一行不是指令名");

                // 不许写空理由：隐藏一条指令永远有具体原因，而理由是唯一能让后来人
                // 判断该不该继续隐藏的东西。空字符串会被策略当成「没隐藏」。
                Assert.Matches("HiddenReason = \"[^\"]+\"", lines[index]);
                declared.Add(name.Groups["name"].Value);
            }
        }

        Assert.Equal(
            new[]
            {
                "vulcan.app.quit",       // 远程客户端不得退出宿主
                "vulcan.command.run",    // 脚本批量执行会绕过逐条工具排除
                "vulcan.module.install", // 运行包变更只走认证的本机通道
                "vulcan.module.remove",
            }.Order(StringComparer.OrdinalIgnoreCase),
            declared.Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>隐藏是描述符自己的声明，不再由指令名推导。</summary>
    /// <remarks>
    /// 按名字写的排除失效过四次，每次都是同一个原因：指令改了名，规则还盯着旧名字。
    /// 这里锁住「策略不再认识任何指令名」这件事本身——
    /// 名字相同而声明不同的两个描述符，必须得到不同的判定。
    /// </remarks>
    [Fact]
    public void ExclusionFollowsTheDescriptorNotTheName()
    {
        var exposed = new CommandDescriptor
        {
            Name = "vulcan.module.install",
            Summary = "同名但未声明隐藏",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };
        var hidden = new CommandDescriptor
        {
            Name = "harmless.read",
            Summary = "名字无害但声明了隐藏",
            Readonly = true,
            HiddenReason = "测试用",
            Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
        };

        Assert.Null(McpExposurePolicy.HardExclusionReason(exposed));
        Assert.Equal("测试用", McpExposurePolicy.HardExclusionReason(hidden));

        // 隐藏的指令在任何策略下都不可见，即便它只读且无害。
        Assert.False(McpExposurePolicy.IsVisible(hidden, "standard"));
        Assert.False(McpExposurePolicy.IsVisible(hidden, "readonly"));
        Assert.Equal("hidden", McpExposurePolicy.State(hidden));
    }

    [Fact]
    public void FloodIsNotPartOfTheShippedBuiltinCatalog()
    {
        // 诊断指令不属于正式命令集：只有把 diagnostics.commands 显式置真的宿主才注册它。
        Assert.DoesNotContain(
            "vulcan.log.flood",
            BuiltinCommandDefinitions.Names,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoSegmentNamesAreClasslessDirectMethods()
    {
        // DEC-025：两段名是「域.方法」，判为无类；首段是域而不是类。
        Assert.Equal(string.Empty, CommandRegistry.LegacyClass("mercury.go"));
        Assert.Equal(string.Empty, CommandRegistry.LegacyClass("fixture.cell"));
        Assert.Equal("core", CommandRegistry.LegacyClass("ping"));
        Assert.Equal("ui", CommandRegistry.LegacyClass("vulcan.ui.dock"));
        Assert.Equal("dock", CommandRegistry.GetMethod("vulcan.ui.dock"));
    }

    [Fact]
    public void ModuleCommandProjectionKeepsClasslessCommandsClassless()
    {
        // 模块指令跨服务边界投影时曾把空类替换为 "core"，使两段式直接方法
        // 在命令集里显示为 core 类（mercury.go 曾复现）。空类必须原样穿过投影。
        var registry = new CommandRegistry();
        registry.Register(
            new CommandDescriptor
            {
                Name = "fixture.go",
                Summary = "classless direct method",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            },
            "module:FixtureModule");

        Assert.Equal(string.Empty, registry.GetCommandClass("fixture.go"));
        Assert.True(CommandClassLabels.IsNone(registry.GetCommandClass("fixture.go")));
        Assert.Equal(
            CommandClassLabels.None,
            CommandClassLabels.Display(registry.GetCommandClass("fixture.go")));
    }

    [Fact]
    public void ClasslessLabelIsDisplayOnly()
    {
        // 标签只做显示层翻译，不参与类推导，两个方向都必须可逆。
        Assert.Equal(CommandClassLabels.None, CommandClassLabels.Display(""));
        Assert.Equal(CommandClassLabels.None, CommandClassLabels.Display(null));
        Assert.Equal("ui", CommandClassLabels.Display("ui"));
        Assert.Equal(string.Empty, CommandClassLabels.ToKey(CommandClassLabels.None));
        Assert.Equal("ui", CommandClassLabels.ToKey("ui"));
        Assert.True(CommandClassLabels.IsNone(CommandRegistry.LegacyClass("mercury.go")));
    }

    [Fact]
    public void RegisteredDomainsComeFromTheRegistryNotAConstantTable()
    {
        var registry = new CommandRegistry();
        Assert.False(registry.IsRegisteredDomain("fixture"));

        registry.Register(
            new CommandDescriptor
            {
                Name = "fixture.go",
                Summary = "classless direct method",
                Handler = CommandDescriptor.Sync(_ => CommandResult.Ok()),
            },
            "framework");

        // 域随注册出现，不需要任何地方登记常量。
        Assert.True(registry.IsRegisteredDomain("fixture"));
        Assert.True(registry.IsRegisteredDomain("FIXTURE"));
        Assert.Contains("fixture", registry.Domains());
        Assert.Equal(string.Empty, registry.GetCommandClass("fixture.go"));
    }

    [Theory]
    // 未聚焦：原样执行。
    [InlineData("proj.list", "全部", "proj.list")]
    // 聚焦 janus：首段不是已注册域 → 补前缀。
    [InlineData("proj.list", "janus", "janus.proj.list")]
    [InlineData("gitrule.scan name=x", "janus", "janus.gitrule.scan name=x")]
    // 聚焦 janus：首段是已注册域 → 绝对名，不补前缀。这就是退出聚焦不需要指令的原因。
    [InlineData("mercury.go", "janus", "mercury.go")]
    [InlineData("vulcan.ui.reset", "janus", "vulcan.ui.reset")]
    [InlineData("janus.proj.list", "janus", "janus.proj.list")]
    public void DomainFocusResolvesAbsoluteNamesWithoutPrefixing(
        string input, string focused, string expected)
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vulcan", "janus", "mercury",
        };

        Assert.Equal(expected, DomainFocus.Resolve(input, focused, registered.Contains));
    }

    [Fact]
    public void DomainFocusLeavesBlankInputAlone()
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "janus", "vulcan" };
        Assert.Equal("", DomainFocus.Resolve("", "janus", registered.Contains));
        Assert.Equal("   ", DomainFocus.Resolve("   ", "janus", registered.Contains));
        Assert.False(DomainFocus.WouldPrefix("", "janus", registered.Contains));
        Assert.True(DomainFocus.WouldPrefix("proj.list", "janus", registered.Contains));
        Assert.False(DomainFocus.WouldPrefix("vulcan.ui.reset", "janus", registered.Contains));
    }
}
