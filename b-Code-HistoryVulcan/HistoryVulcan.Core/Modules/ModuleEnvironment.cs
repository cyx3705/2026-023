// 统一契约里的运行环境与总线事件（5.9.0，DEC-070）。
//
// 5.0 起宿主不给模块数据目录，结果各模块各拼各的路径（至少 5 种布局），宿主一动运行区就要逐个核对。
// 现在由宿主给出：路径归宿主，内容归模块。事件是总线的另一半：指令是「问」，事件是「告知」。

using System.Text.Json;

namespace HistoryVulcan.Core.Modules;

/// <summary>宿主当前的运行方式。</summary>
public enum HostRunMode
{
    /// <summary>正式后台服务（登录启动的 HistoryVulcan.exe）。</summary>
    Service = 0,

    /// <summary>
    /// 一次性离线组合：命令行 <c>HistoryVulcan.Cli.exe --cli</c> 与无头导出 <c>--export-command-manual</c>，
    /// 做完一件事就退出。模块在这种方式下不应开端口或改写对外通告文件。
    /// </summary>
    OfflineCli = 1,

    /// <summary>测试装载（HistoryVulcan.Cli.exe --probe），只装一个候选包。</summary>
    Probe = 2,
}

/// <summary>宿主交给模块的运行环境。</summary>
public interface IModuleEnvironment
{
    /// <summary>本模块的名字（manifest name）。</summary>
    string ModuleName { get; }

    /// <summary>
    /// 本模块的可写数据目录。独立于包槽位：装包、热重载、卸载都不动它，只有
    /// <c>vulcan.module.uninstall purge=true</c> 才删除。目录由宿主创建。
    /// </summary>
    string DataDirectory { get; }

    /// <summary>
    /// 本模块的完整包目录（运行区槽位，只读）。要找随包发布的文件（如外部可执行程序）就从这里拼，
    /// 不要从宿主数据根推运行区布局，也不要用 <c>Assembly.Location</c>——宿主从内存流装载程序集，它是空的。
    /// 不要往里写：槽位里出现清单外的文件，下一轮发现就判为坏包。（6.0.0）
    /// </summary>
    string PackageDirectory { get; }

    /// <summary>宿主当前的运行方式。</summary>
    HostRunMode RunMode { get; }

    /// <summary>宿主版本（三段语义化版本）。</summary>
    string HostVersion { get; }
}

/// <summary>总线上的一条事件。事件是通知不是调用：要细节就再执行只读指令。</summary>
public sealed class BusEvent
{
    /// <summary>构造一条事件；由宿主的事件中枢创建。</summary>
    public BusEvent(string topic, string source, DateTimeOffset time, JsonElement payload)
    {
        Topic = topic;
        Source = source;
        Time = time;
        Payload = payload;
    }

    /// <summary>具名主题，前缀是发布方的指令域（宿主为 <c>vulcan.</c>）。</summary>
    public string Topic { get; }

    /// <summary>发布方：<c>host</c> 或 <c>module:&lt;模块名&gt;</c>。</summary>
    public string Source { get; }

    /// <summary>发布时间。</summary>
    public DateTimeOffset Time { get; }

    /// <summary>JSON 载荷；契约是 JSON 形状，不是任何 CLR 类型。</summary>
    public JsonElement Payload { get; }
}
