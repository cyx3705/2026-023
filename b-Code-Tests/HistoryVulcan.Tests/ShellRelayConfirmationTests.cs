using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using HistoryVulcan.Services.Web;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// REQ-A2：服务进程不再自己弹确认框，改为中继到前端。
///
/// 这里守的是**失败方向**：没有前端可问时必须拒绝。宿主无头化后，若这条退化成放行，
/// MCP 侧「危险命令需确认」（vulcan.app.quit、module.install/remove、全部 mcp.*）
/// 整条约束就会在无人值守时静默失效——而且不会有任何测试或日志提示它失效了。
/// </summary>
[Collection(TestCollections.Gateway)]
public sealed class ShellRelayConfirmationTests
{
    [Fact]
    public void DeniesWhenGatewayIsNotRunning()
    {
        var log = new RecordingLog();
        using var gateway = NewGateway(log);
        var confirmation = new ShellRelayConfirmation(() => gateway, log);

        Assert.False(confirmation.Confirm("删除运行区模块？"));
        Assert.Contains(log.Messages, message => message.Contains("网关未运行"));
    }

    [Fact]
    public void DeniesWhenNoFrontendIsConnected()
    {
        var log = new RecordingLog();
        using var gateway = NewGateway(log);
        Assert.True(gateway.Start(FreePort()).Success);
        var confirmation = new ShellRelayConfirmation(() => gateway, log);

        Assert.False(confirmation.Confirm("删除运行区模块？"));
        Assert.Contains(log.Messages, message => message.Contains("前端未连接"));
    }

    [Fact]
    public void DeniesWhenGatewayIsAbsentEntirely()
    {
        var log = new RecordingLog();
        var confirmation = new ShellRelayConfirmation(() => null, log);

        Assert.False(confirmation.Confirm("删除运行区模块？"));
    }

    /// <summary>远程确认走同一条判定，超时秒数只影响等待时长，不影响"无前端即拒绝"。</summary>
    [Fact]
    public void RemoteConfirmationFollowsTheSameDenyPath()
    {
        var log = new RecordingLog();
        using var gateway = NewGateway(log);
        Assert.True(gateway.Start(FreePort()).Success);
        var confirmation = new ShellRelayConfirmation(() => gateway, log);

        Assert.False(confirmation.ConfirmRemote("cursor", "退出宿主？", timeoutSeconds: 1));
    }

    /// <summary>只记消息文本的日志：断言的是"拒绝时留下了可见痕迹"，与条目结构无关。</summary>
    private sealed class RecordingLog : IShellLog
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public void Log(ShellLogLevel level, string category, string message) => _messages.Add(message);
        public event EventHandler<ShellLogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<ShellLogEntry> Snapshot() => [];
    }

    private static WebGateway NewGateway(IShellLog log)
        => new(() => new CommandBus(new CommandRegistry(), log), new MemorySettings(), log);

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
