using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 服务侧 `vulcan.app.get/set` 的键白名单与脱敏（REQ-A8 补位）。
///
/// 前端整体迁出后，原先覆盖「任意配置键脱敏」的用例随被测实现搬去了 Aurora
/// （那份测的是前端 `BuiltinCommands` 注册的版本）。宿主这份由 `ServiceComposer`
/// 提供，契约窄得多——**只接受 `mcp.*` 键**（REQ-MCP-002）。
///
/// 补这条是为了不让迁移在宿主侧留下覆盖真空：搬走一份实现的测试时，
/// 必须确认留下的那份实现仍然有人守。
/// </summary>
public sealed class ServiceSettingRedactionTests
{
    [Fact]
    public async Task BackendSettingEntryRejectsKeysOutsideTheMcpNamespace()
    {
        var (bus, _) = NewBus(settings => settings.Set("web.token", "frontend-secret"));

        var rejected = await bus.ExecuteAsync("vulcan.app.get key=web.token", "Test");

        Assert.False(rejected.Success);
        Assert.DoesNotContain("frontend-secret", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendSettingReadNeverEchoesTheSecretValue()
    {
        var (bus, _) = NewBus(settings => settings.Set("mcp.token", "backend-secret"));

        var read = await bus.ExecuteAsync("vulcan.app.get key=mcp.token", "Test");

        Assert.True(read.Success, read.Message);
        Assert.DoesNotContain("backend-secret", read.Message, StringComparison.Ordinal);
    }

    /// <summary>写入回执同样不得回显——回执会原样进 HTTP 响应体与 tools/call 载荷。</summary>
    [Fact]
    public async Task BackendSettingWriteNeverEchoesTheSecretValue()
    {
        var (bus, _) = NewBus();

        var write = await bus.ExecuteAsync("vulcan.app.set key=mcp.token value=echo-secret", "Test");

        Assert.True(write.Success, write.Message);
        Assert.DoesNotContain("echo-secret", write.Message, StringComparison.Ordinal);
    }

    private static (CommandBus Bus, MemoryLog Log) NewBus(Action<ISettingsService>? seed = null)
    {
        var settings = new MemorySettings();
        seed?.Invoke(settings);
        var log = new MemoryLog();
        var registry = new CommandRegistry();
        var bus = new CommandBus(registry, log);
        ServiceComposer.RegisterServiceMcpSettingCommands(registry, settings);
        return (bus, log);
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];
        public void Log(ShellLogLevel level, string category, string message)
            => _entries.Add(new ShellLogEntry(DateTime.Now, level, category, message));
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries;
    }
}
