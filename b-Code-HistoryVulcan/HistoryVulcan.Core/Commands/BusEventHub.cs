using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;

namespace HistoryVulcan.Core.Commands;

/// <summary>
/// 总线事件中枢（5.9.0，DEC-070）：具名主题的发布与订阅。
/// </summary>
/// <remarks>
/// <para>事件是通知，不是调用。发布方不等待、不知道谁在听；订阅方要细节就再执行只读指令。</para>
/// <para>
/// 每个订阅有自己的串行队列：同一订阅按发布顺序收到事件（控制台显示日志靠这一点），
/// 不同订阅之间互不阻塞。处理器抛出的异常记进 <see cref="LogCategory"/>，
/// 宿主转发日志事件时跳过这一类，免得「处理器出错 → 记日志 → 再投给处理器」绕圈。
/// </para>
/// </remarks>
internal sealed partial class BusEventHub
{
    /// <summary>事件中枢自己的日志类别；宿主不把这一类转发成 <c>vulcan.log.entry</c>。</summary>
    internal const string LogCategory = "bus.event";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IShellLog _log;
    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];

    public BusEventHub(IShellLog log) => _log = log;

    /// <summary>主题名：小写字母、数字、连字符组成的段，以点分隔，至少两段。</summary>
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)+$")]
    private static partial Regex TopicPattern();

    internal static bool IsValidTopic(string topic) => TopicPattern().IsMatch(topic);

    /// <param name="topic">完整主题，或以 <c>.*</c> 结尾的前缀。</param>
    /// <param name="owner">订阅方：<c>host</c> 或模块名；模块卸载时按它整批退订。</param>
    /// <param name="handler">处理器。</param>
    public IDisposable Subscribe(string topic, string owner, Action<BusEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var value = (topic ?? "").Trim();
        var prefix = value.EndsWith(".*", StringComparison.Ordinal);
        var name = prefix ? value[..^2] : value;
        if (!IsValidTopic(name) && !(prefix && Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]*$")))
            throw new ArgumentException($"事件主题不合法：{topic}（小写点分段，前缀订阅以 .* 结尾）", nameof(topic));

        var subscription = new Subscription(this, name, prefix, owner, handler);
        lock (_gate)
            _subscriptions.Add(subscription);
        return subscription;
    }

    /// <param name="topic">完整主题名。</param>
    /// <param name="source">发布方：<c>host</c> 或 <c>module:&lt;名&gt;</c>。</param>
    /// <param name="payload">载荷，按 JSON（camelCase）序列化；null 发布空对象。</param>
    public void Publish(string topic, string source, object? payload)
    {
        if (!IsValidTopic(topic))
            throw new ArgumentException($"事件主题不合法：{topic}", nameof(topic));

        Subscription[] targets;
        lock (_gate)
            targets = _subscriptions.Where(item => item.Matches(topic)).ToArray();
        if (targets.Length == 0)
            return;

        var element = payload is JsonElement json
            ? json.Clone()
            : JsonSerializer.SerializeToElement(payload ?? new { }, JsonOptions);
        var evt = new BusEvent(topic, source, DateTimeOffset.Now, element);
        foreach (var target in targets)
            target.Enqueue(evt);
    }

    /// <summary>模块卸载时撤掉它的全部订阅。</summary>
    public int RemoveOwner(string owner)
    {
        lock (_gate)
        {
            var removed = _subscriptions.RemoveAll(item =>
                item.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase));
            return removed;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _subscriptions.Count;
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
            _subscriptions.Remove(subscription);
    }

    private sealed class Subscription(
        BusEventHub hub, string topic, bool prefix, string owner, Action<BusEvent> handler) : IDisposable
    {
        private readonly ConcurrentQueue<BusEvent> _queue = new();
        private int _draining;
        private volatile bool _disposed;

        public string Owner { get; } = owner;

        public bool Matches(string candidate)
            => !_disposed && (prefix
                ? candidate.StartsWith(topic + ".", StringComparison.Ordinal)
                : candidate.Equals(topic, StringComparison.Ordinal));

        public void Enqueue(BusEvent evt)
        {
            _queue.Enqueue(evt);
            if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
                ThreadPool.QueueUserWorkItem(_ => Drain());
        }

        private void Drain()
        {
            while (true)
            {
                while (!_disposed && _queue.TryDequeue(out var evt))
                {
                    try
                    {
                        handler(evt);
                    }
                    catch (Exception ex)
                    {
                        hub._log.Warn(LogCategory, $"{Owner} 处理 {evt.Topic} 时出错：{ex.GetType().Name}: {ex.Message}");
                    }
                }

                Volatile.Write(ref _draining, 0);
                if (_disposed || _queue.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
                    return;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            hub.Remove(this);
        }
    }
}
