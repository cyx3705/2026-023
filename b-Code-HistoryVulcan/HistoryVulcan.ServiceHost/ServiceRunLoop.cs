using System.Collections.Concurrent;

namespace HistoryVulcan.ServiceHost;

/// <summary>
/// 服务进程的单线程消息循环（4.0.0，REQ-A4）。
///
/// 取代此前的 WPF <c>Application</c> + <c>Dispatcher</c>。服务进程从来没有窗口，
/// 用 WPF 只是为了拿一个"把活儿排到主线程"的队列——代价是整个工程被迫 <c>UseWPF</c>，
/// 并连带拖入 PresentationFramework 等一整套 UI 程序集。
///
/// 语义上要与被取代者对齐的三点：
/// <list type="number">
/// <item>安装为 <see cref="SynchronizationContext"/>，使 <c>CommandBus.UiContext</c> 与
/// <c>ModuleHost.UiContext</c> 的编组语义不变——模块仍可假定"投递到 UiContext 的回调
/// 彼此串行、且不与另一个回调重入"。</item>
/// <item><see cref="Post"/> 异步排队，<see cref="Send"/> 同步等待，与
/// <c>Dispatcher.BeginInvoke</c> / <c>Invoke</c> 对应。在循环线程上调用
/// <see cref="Send"/> 直接执行，避免自锁——<c>Dispatcher.Invoke</c> 也是这个行为。</item>
/// <item><see cref="Shutdown"/> 对应 <c>ShutdownMode.OnExplicitShutdown</c>：
/// 只有显式调用才会退出，队列排空后循环结束。</item>
/// </list>
///
/// 不实现 WPF 的优先级队列。原先唯一用到优先级的地方是把延迟启动任务排在
/// <c>ApplicationIdle</c>，其真实意图是"让它排在已入队的启动工作之后"，
/// 而 FIFO 队列天然满足——见 <c>ServiceHost.Run</c> 的入队顺序。
/// </summary>
internal sealed class ServiceRunLoop : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(new ConcurrentQueue<Action>());
    private readonly int _loopThreadId;
    private int _exitCode;
    private int _disposed;

    internal ServiceRunLoop() => _loopThreadId = Environment.CurrentManagedThreadId;

    /// <summary>循环是否已被要求退出。</summary>
    internal bool IsShuttingDown => _queue.IsAddingCompleted;

    /// <summary>把回调排入循环，不等待。</summary>
    internal void Post(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        // 关停与入队存在天然竞态：CompleteAdding 之后到达的投递只能丢弃。
        // 这不是错误路径——关停期间的清理回调本就不该再改变状态。
        // ObjectDisposedException 派生自 InvalidOperationException，必须先捕子类。
        try { _queue.Add(callback); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>把回调排入循环并等待完成；在循环线程上调用则直接执行。</summary>
    internal void Send(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (Environment.CurrentManagedThreadId == _loopThreadId)
        {
            callback();
            return;
        }

        using var done = new ManualResetEventSlim(false);
        ExceptionDispatchInfoHolder holder = new();
        Post(() =>
        {
            try { callback(); }
            catch (Exception ex) { holder.Error = ex; }
            finally { done.Set(); }
        });
        done.Wait();
        if (holder.Error != null)
            throw new InvalidOperationException("循环回调执行失败", holder.Error);
    }

    /// <summary>请求退出；队列排空后 <see cref="Run"/> 返回。</summary>
    internal void Shutdown(int exitCode = 0)
    {
        Interlocked.Exchange(ref _exitCode, exitCode);
        try { _queue.CompleteAdding(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>在当前线程跑循环，直到 <see cref="Shutdown"/> 且队列排空。</summary>
    internal int Run(Action<Exception> onCallbackFailed)
    {
        ArgumentNullException.ThrowIfNull(onCallbackFailed);
        foreach (var callback in _queue.GetConsumingEnumerable())
        {
            // 单个回调抛异常不得终结整个服务进程——与 Dispatcher 未处理异常被
            // App 级处理器吞掉后继续泵消息的既有行为一致。
            try { callback(); }
            catch (Exception ex) { onCallbackFailed(ex); }
        }
        return Volatile.Read(ref _exitCode);
    }

    /// <summary>接口实现必须公开；类型本身是 internal，不构成公开面。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Shutdown();
        _queue.Dispose();
    }

    private sealed class ExceptionDispatchInfoHolder
    {
        public Exception? Error;
    }
}

/// <summary>把 <see cref="SynchronizationContext"/> 投递转到 <see cref="ServiceRunLoop"/>。</summary>
internal sealed class ServiceRunLoopSynchronizationContext : SynchronizationContext
{
    private readonly ServiceRunLoop _loop;

    internal ServiceRunLoopSynchronizationContext(ServiceRunLoop loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        _loop = loop;
    }

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _loop.Post(() => d(state));
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _loop.Send(() => d(state));
    }

    public override SynchronizationContext CreateCopy() => new ServiceRunLoopSynchronizationContext(_loop);
}
