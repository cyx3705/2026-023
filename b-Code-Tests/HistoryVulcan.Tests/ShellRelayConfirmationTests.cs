using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Storage;
using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// REQ-A2：服务进程不再自己弹确认框。
///
/// 这里守的是**失败方向**：没有界面可问时必须拒绝。若这条退化成放行，
/// MCP 侧「危险命令需确认」（vulcan.app.quit、module.install/remove、全部 mcp.*）
/// 整条约束就会在无人值守时静默失效——而且不会有任何测试或日志提示它失效了。
///
/// 4.2.0 起宿主自带的实现一律拒绝，能弹框的是承载界面的模块（Aurora 在装载时
/// 换掉 Bus.Confirmation）。进程外前端的 WebSocket 中继随 DEC-008 一并退役——
/// 它在无连接时同样是拒绝，所以本文件守的属性一个字都没变。
/// </summary>
public sealed class ShellRelayConfirmationTests
{
    [Fact]
    public void DeniesAndLeavesATrace()
    {
        var log = new RecordingLog();
        var confirmation = new ShellRelayConfirmation(log);

        Assert.False(confirmation.Confirm("删除运行区模块？"));
        Assert.Contains(log.Messages, message => message.Contains("界面未装载"));
    }

    /// <summary>远程确认走同一条判定，超时秒数不影响「无界面即拒绝」。</summary>
    [Fact]
    public void RemoteConfirmationFollowsTheSameDenyPath()
    {
        var log = new RecordingLog();
        var confirmation = new ShellRelayConfirmation(log);

        Assert.False(confirmation.ConfirmRemote("cursor", "退出宿主？", timeoutSeconds: 1));
        Assert.Contains(log.Messages, message => message.Contains("cursor"));
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

    private sealed class MemorySettings : ISettingsService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
        public string? Get(string key) => _values.GetValueOrDefault(key);
        public int GetInt(string key, int fallback) => int.TryParse(Get(key), out var value) ? value : fallback;
        public void Set(string key, string value) => _values[key] = value;
        public IReadOnlyList<KeyValuePair<string, string>> All() => _values.ToList();
    }
}
