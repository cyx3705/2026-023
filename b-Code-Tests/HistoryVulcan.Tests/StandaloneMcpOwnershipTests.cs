using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;
using HistoryVulcan.ServiceHost;

namespace HistoryVulcan.Tests;

/// <summary>
/// 后台对 MCP 的所有权（REQ-MCP-002）。
///
/// REQ-A8：原第一条用例 StandaloneFrontendDelegatesMcpOwnershipToBackend 断言
/// 前端配置 EnableMcp=false，被测对象随前端迁往 Aurora，该断言现由
/// Aurora 的 FrontendMcpOwnershipTests 承担。本文件保留的三条都直接测
/// ServiceComposer，是宿主自己的职责。
/// </summary>
public sealed class StandaloneMcpOwnershipTests
{

    [Fact]
    public async Task BackendSettingCommandsOnlyAcceptMcpKeysAndMaskSecrets()
    {
        var registry = new CommandRegistry();
        var settings = new MemorySettings();
        ServiceComposer.RegisterServiceMcpSettingCommands(registry, settings);
        var bus = new CommandBus(registry, new NullLog());

        var rejected = await bus.ExecuteAsync(
            "vulcan.app.set key=web.port value=9000", "Test");
        Assert.False(rejected.Success);
        Assert.Null(settings.Get("web.port"));

        var accepted = await bus.ExecuteAsync(
            "vulcan.app.set key=mcp.token value=top-secret", "Test");
        var read = await bus.ExecuteAsync("vulcan.app.get key=mcp.token", "Test");
        Assert.True(accepted.Success, accepted.Message);
        Assert.True(read.Success, read.Message);
        Assert.Equal("top-secret", settings.Get("mcp.token"));
        Assert.Contains("(已配置)", read.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", read.Message, StringComparison.Ordinal);

        // REQ-A8：原先此处还断言 App.IsBackendMcpSettingCommand 的路由判定，
        // 该方法随前端迁往 Aurora，断言一并移交 FrontendMcpOwnershipTests。
        // 本文件只保留后台侧的键白名单与脱敏。
    }

    [Fact]
    public void LegacyMcpMigrationOnlyFillsMissingServiceValues()
    {
        var legacy = new MemorySettings();
        legacy.Set("mcp.port", "8737");
        legacy.Set("mcp.policy", "standard");
        legacy.Set("web.port", "8938");
        var service = new MemorySettings();
        service.Set("mcp.policy", "readonly");

        ServiceComposer.MigrateLegacyMcpSettings(legacy, service, new NullLog());

        Assert.Equal("8737", service.Get("mcp.port"));
        Assert.Equal("readonly", service.Get("mcp.policy"));
        Assert.Null(service.Get("web.port"));
    }

    /// <summary>
    /// 宿主不得再持有任何对外监听。
    /// </summary>
    /// <remarks>
    /// 4.3.0 迁出 Web、4.4.0 迁出 MCP 之后，「先装模块再开监听」这条顺序不变量在宿主侧
    /// 已经无处可守——宿主一个监听器都不开了。它在模块侧也没有换个地方重新成立：
    /// 网关随模块装载过程打开，而不是全部装完之后，因此重载期间客户端会先遇到连接被拒，
    /// 随后极短一段窗口内可能读到尚未提交的指令目录。代价由客户端重试吸收。
    ///
    /// 于是这条用例改守一件更简单也更要紧的事：**监听不许回到宿主**。
    /// 哪天有人为了图方便把网关加回 ServiceHost，这条立刻失败。
    /// </remarks>
    [Fact]
    public void ServiceHostOpensNoRemoteListenersOfItsOwn()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "b-Code-HistoryVulcan",
            "src",
            "HistoryVulcan.ServiceHost",
            "ServiceHost.cs"));

        Assert.Contains("composition.Modules?.Start()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("composition.Web", source, StringComparison.Ordinal);
        Assert.DoesNotContain("composition.Mcp", source, StringComparison.Ordinal);
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

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class NullLog : IShellLog
    {
        public void Log(ShellLogLevel level, string category, string message) { }
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }
}
