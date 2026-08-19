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

    [Fact]
    public void DiagnosticFloodCommandStaysOutOfReachOfRemoteClients()
    {
        // 3.3.2 把 debug.logflood 收编为 vulcan.log.flood。原先的 MCP 硬排除按 "debug." 前缀
        // 生效，改名一度让它失效——承压注水因此可被 MCP/Web 远程触发
        // （rate=100000 × seconds=600 即 6000 万条日志）。这里锁住修复后的行为。
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("vulcan.log.flood"));
        Assert.Contains("vulcan.log.flood", McpExposurePolicy.DiagnosticCommandNames);

        // 前缀规则保留给尚未迁移的模块调试指令。
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("debug.anything"));

        // 同类的日志指令不受影响，仍可正常暴露。
        Assert.Null(McpExposurePolicy.HardExclusionReason("vulcan.log.level"));
    }

    [Fact]
    public void RuntimePackageMutationCommandsStayOutOfReachOfRemoteClients()
    {
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("vulcan.module.install"));
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("vulcan.module.remove"));
        Assert.Null(McpExposurePolicy.HardExclusionReason("vulcan.module.reload"));
    }

    /// <summary>
    /// REQ-A6 的连带约束。`vulcan.command.run` 从前端搬到服务侧后自动变成 MCP 可见工具
    /// （真机实测确认过），而它按路径读本地脚本并逐行执行任意命令——只要磁盘上存在一个
    /// 脚本文件，远程就能一次性执行其中任意命令，包括本表其他条目明确排除的那些。
    /// 这条断言守的是"批量执行入口不得成为逐条排除的旁路"。
    /// </summary>
    [Fact]
    public void ScriptBatchExecutionCannotBypassPerCommandExclusions()
    {
        Assert.NotNull(McpExposurePolicy.HardExclusionReason("vulcan.command.run"));

        // 对照：同属 command 类的只读查询命令不受影响。
        Assert.Null(McpExposurePolicy.HardExclusionReason("vulcan.command.list"));
        Assert.Null(McpExposurePolicy.HardExclusionReason("vulcan.command.show"));
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
