using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace HistoryVulcan.Tests;

/// <summary>
/// WPF 测试宿主的唯一所有者：STA 线程与「推进 UI」的全部原语都收在这里。
///
/// 3.3.2 之前 <c>RunSta</c> 与 <c>PumpDispatcher</c> 在三个测试文件里各有一份拷贝，
/// 且 <c>PumpDispatcher</c> 无条件 <b>固定睡 300ms</b>——不管界面是否早已空闲。
/// 43 个默认调用点叠加后，仅这一处就占掉整轮测试约三分之二的墙钟时间。
///
/// 现在按「测试到底在等什么」分成三个原语，各自表达明确意图：
/// <list type="bullet">
///   <item><see cref="Pump"/>：只需把已排队的界面工作做完 —— 排空即返回，不睡固定时长。绝大多数场景用它。</item>
///   <item><see cref="PumpUntil"/>：在等某个异步结果 —— 边推进边轮询条件，条件成立立即返回。</item>
///   <item><see cref="PumpFor"/>：确实需要真实时间流逝（控制台 100ms 批量合并、计时器）—— 仅此场景才等待。</item>
/// </list>
/// </summary>
internal static class UiTestHost
{
    /// <summary>单个 UI 用例的硬上限。超过即判定挂死，不再无声等待。</summary>
    private const int StaTimeoutMilliseconds = 60_000;

    /// <summary>
    /// 在 STA 线程上运行 UI 测试体，异常按原栈重抛。
    ///
    /// <b>不会无限等待</b>：3.3.2 之前这里是裸 <c>Join()</c>，一旦用例阻塞
    /// （模态对话框、跨进程 mutex 被占、端口冲突），整轮测试就静默停住——
    /// 曾经出现过 25 分钟零输出、testhost 累计 CPU 仅 9.5 秒的情况，
    /// CI 上则表现为 job 超时被杀且日志里什么都看不出来。
    /// 现在超时会变成一条指名道姓的失败（DEC-023）。
    /// </summary>
    public static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                // 收尾排空，避免未处理的排队回调泄漏到下一个用例。
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            // 挂死时不要拖住进程退出：后台线程不阻止 testhost 结束。
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(StaTimeoutMilliseconds))
        {
            throw new TimeoutException(
                $"UI 测试体在 {StaTimeoutMilliseconds / 1000} 秒内未完成，判定为挂死。" +
                "常见原因：命令处理器弹出了模态对话框（REQ-CMD-012 禁止）、" +
                "本机有 HistoryVulcan 实例占用 ServiceHost 全局 mutex 或 MCP/Web 端口。");
        }

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>
    /// 把当前已排队的界面工作做完后立即返回。
    /// 在 <see cref="DispatcherPriority.ApplicationIdle"/> 排一个空操作：它被执行时，
    /// 所有更高优先级的渲染、布局和回调都已处理完毕。
    /// </summary>
    public static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 边推进界面边等待 <paramref name="condition"/> 成立；成立即返回 true，超时返回 false。
    /// 用于等异步命令、后台任务或日志批量刷新的结果，取代「睡够久应该就好了」。
    /// </summary>
    public static bool PumpUntil(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (condition())
                return true;

            // 让出时间片给后台线程推进，同时保持 UI 队列继续流转。
            var idle = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(10),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                idle.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(idle);
        }

        Pump();
        return condition();
    }

    /// <summary>
    /// 推进界面并等待真实时间流逝。只在测试对象本身依赖时间时使用
    /// （控制台 100ms 批量合并、双击与按住计时窗口等）。
    /// </summary>
    public static void PumpFor(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Pump();
    }
}
