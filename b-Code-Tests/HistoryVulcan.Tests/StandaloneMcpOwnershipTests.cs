using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using Xunit;
using StandaloneApp = HistoryVulcan.App.App;

namespace HistoryVulcan.Tests;

public sealed class StandaloneMcpOwnershipTests
{
    [Fact]
    public void StandaloneFrontendDelegatesMcpOwnershipToBackend()
    {
        var config = StandaloneApp.CreateStandaloneFrontendConfig(
            new HistoryVulcan.Core.ApplicationIdentity(
                "HistoryVulcan", "3.5.0", "3.5.0", "3.5.0.0"));

        Assert.False(config.EnableMcp);
        Assert.True(config.EnableRemoteManagementViews);
        Assert.False(config.EnableModules);

        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "b-Code-HistoryVulcan", "src", "App", "App.xaml.cs"));
        Assert.Contains("new Services.Mcp.McpGateway(", source, StringComparison.Ordinal);
        Assert.Contains("McpCommands.RegisterAll(", source, StringComparison.Ordinal);
        Assert.Contains("Mcp = mcp", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendSettingCommandsOnlyAcceptMcpKeysAndMaskSecrets()
    {
        var registry = new CommandRegistry();
        var settings = new MemorySettings();
        StandaloneApp.RegisterServiceMcpSettingCommands(registry, settings);
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

        Assert.True(StandaloneApp.IsBackendMcpSettingCommand(
            CommandParser.Parse("vulcan.app.get key=mcp.policy")));
        Assert.False(StandaloneApp.IsBackendMcpSettingCommand(
            CommandParser.Parse("vulcan.app.get key=web.port")));
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

        StandaloneApp.MigrateLegacyMcpSettings(legacy, service, new NullLog());

        Assert.Equal("8737", service.Get("mcp.port"));
        Assert.Equal("readonly", service.Get("mcp.policy"));
        Assert.Null(service.Get("web.port"));
    }

    [Fact]
    public void ServiceStartsModulesBeforeOpeningRemoteListeners()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(),
            "b-Code-HistoryVulcan",
            "src",
            "HistoryVulcan.ServiceHost",
            "ServiceHost.cs"));
        var modules = source.IndexOf("composition.Modules?.Start()", StringComparison.Ordinal);
        var web = source.IndexOf("composition.Web.Start()", StringComparison.Ordinal);
        var mcp = source.IndexOf("composition.Mcp.TryAutostart()", StringComparison.Ordinal);

        Assert.True(modules >= 0);
        Assert.True(web > modules);
        Assert.True(mcp > modules);
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
