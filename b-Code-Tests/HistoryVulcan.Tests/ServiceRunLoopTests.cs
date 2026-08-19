using HistoryVulcan.ServiceHost;
using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// REQ-A4：服务进程的消息循环不再是 WPF Dispatcher。
///
/// 这里守的是被取代者的语义，而不是新实现的内部结构。服务进程把这个循环装成
/// <c>CommandBus.UiContext</c> 与 <c>ModuleHost.UiContext</c>，模块据此假定
/// "投递到 UiContext 的回调彼此串行、不重入"——若换实现时丢了串行性，
/// 症状会是模块里罕见且难复现的竞态，而不是一条构建错误。
/// </summary>
public sealed class ServiceRunLoopTests
{
    [Fact]
    public void RunsQueuedCallbacksInOrderThenExitsOnShutdown()
    {
        using var loop = new ServiceRunLoop();
        var order = new List<int>();

        loop.Post(() => order.Add(1));
        loop.Post(() => order.Add(2));
        loop.Post(() => order.Add(3));
        loop.Post(() => loop.Shutdown());

        var exit = loop.Run(_ => { });

        Assert.Equal([1, 2, 3], order);
        Assert.Equal(0, exit);
    }

    /// <summary>回调在同一线程上串行执行——模块的重入假设依赖这一点。</summary>
    [Fact]
    public void ExecutesCallbacksSeriallyOnTheLoopThread()
    {
        using var loop = new ServiceRunLoop();
        var threads = new List<int>();
        var concurrent = 0;
        var maxConcurrent = 0;

        for (var i = 0; i < 50; i++)
        {
            loop.Post(() =>
            {
                var now = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, now);
                threads.Add(Environment.CurrentManagedThreadId);
                Interlocked.Decrement(ref concurrent);
            });
        }
        loop.Post(() => loop.Shutdown());

        loop.Run(_ => { });

        Assert.Equal(50, threads.Count);
        Assert.Single(threads.Distinct());
        Assert.Equal(1, maxConcurrent);
    }

    /// <summary>单个回调抛异常不得终结整个循环——否则一个坏模块能拖垮服务进程。</summary>
    [Fact]
    public void OneFailingCallbackDoesNotStopTheLoop()
    {
        using var loop = new ServiceRunLoop();
        var failures = new List<Exception>();
        var ranAfterFailure = false;

        loop.Post(() => throw new InvalidOperationException("boom"));
        loop.Post(() => ranAfterFailure = true);
        loop.Post(() => loop.Shutdown());

        loop.Run(failures.Add);

        Assert.True(ranAfterFailure);
        Assert.Single(failures);
        Assert.Equal("boom", failures[0].Message);
    }

    [Fact]
    public void ShutdownPropagatesExitCode()
    {
        using var loop = new ServiceRunLoop();
        loop.Post(() => loop.Shutdown(2));

        Assert.Equal(2, loop.Run(_ => { }));
    }

    /// <summary>关停之后到达的投递被丢弃，不抛异常——关停期间的清理回调本就不该再改状态。</summary>
    [Fact]
    public void PostAfterShutdownIsDiscardedWithoutThrowing()
    {
        using var loop = new ServiceRunLoop();
        loop.Post(() => loop.Shutdown());
        loop.Run(_ => { });

        var ran = false;
        loop.Post(() => ran = true);

        Assert.True(loop.IsShuttingDown);
        Assert.False(ran);
    }

    /// <summary>在循环线程上 Send 必须直接执行，否则自锁——Dispatcher.Invoke 也是这个行为。</summary>
    [Fact]
    public void SendOnTheLoopThreadRunsInlineInsteadOfDeadlocking()
    {
        using var loop = new ServiceRunLoop();
        var ran = false;

        loop.Post(() =>
        {
            loop.Send(() => ran = true);
            loop.Shutdown();
        });

        loop.Run(_ => { });

        Assert.True(ran);
    }

    /// <summary>装成 SynchronizationContext 后，Post 仍落到同一条串行队列上。</summary>
    [Fact]
    public void SynchronizationContextPostsOntoTheSameLoop()
    {
        using var loop = new ServiceRunLoop();
        var context = new ServiceRunLoopSynchronizationContext(loop);
        var seen = new List<string>();

        context.Post(_ => seen.Add("a"), null);
        context.Post(_ => seen.Add("b"), null);
        loop.Post(() => loop.Shutdown());

        loop.Run(_ => { });

        Assert.Equal(["a", "b"], seen);
    }
}
