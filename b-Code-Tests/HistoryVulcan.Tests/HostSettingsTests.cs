using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class HostSettingsTests
{
    [Theory]
    [InlineData("mcp.token")]
    [InlineData("database.password")]
    [InlineData("api_secret")]
    [InlineData("private-key")]
    [InlineData("db.connection-string")]
    [InlineData("passwd")]
    [InlineData("code")]
    public async Task GenericSettingsMaskSensitiveReadWriteAndList(string key)
    {
        var registry = new CommandRegistry();
        var settings = new MemorySettings();
        settings.Set("existing.setting", "retained");
        ServiceComposer.RegisterSettingCommands(registry, settings);
        var bus = new CommandBus(registry, new NullLog());

        var ordinary = await bus.ExecuteAsync("vulcan.app.set key=module.theme value=dark", "Test");
        Assert.True(ordinary.Success, ordinary.Message);
        Assert.Equal("dark", settings.Get("module.theme"));
        var accepted = await bus.ExecuteAsync($"vulcan.app.set key={key} value=top-secret", "Test");
        var read = await bus.ExecuteAsync($"vulcan.app.get key={key}", "Test");
        var list = await bus.ExecuteAsync("vulcan.app.get", "Test");
        Assert.Equal("top-secret", settings.Get(key));
        Assert.Equal("retained", settings.Get("existing.setting"));
        foreach (var result in new[] { accepted, read, list })
        {
            Assert.True(result.Success, result.Message);
            Assert.Contains("(已配置)", result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("top-secret", result.Message, StringComparison.Ordinal);
        }
        Assert.Contains("module.theme = dark", list.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingFailuresDoNotEchoSensitiveValues()
    {
        var registry = new CommandRegistry();
        var settings = new MemorySettings { FailWrites = true };
        ServiceComposer.RegisterSettingCommands(registry, settings);
        var bus = new CommandBus(registry, new NullLog());
        var result = await bus.ExecuteAsync("vulcan.app.set database.password input-secret", "Test");
        Assert.False(result.Success);
        Assert.DoesNotContain("input-secret", result.Message, StringComparison.Ordinal);
        Assert.Null(settings.Get("database.password"));
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        internal bool FailWrites { get; init; }
        public void Set(string key, string value)
        {
            if (FailWrites)
                throw new IOException("write failed for " + value);
            _values[key] = value;
        }
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

}
