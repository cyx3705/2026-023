using Xunit;

namespace HistoryVulcan.Tests;

/// <summary>
/// 测试集合按「争用哪种独占资源」划分（DEC-023）。
///
/// 3.3.2 之前整个程序集挂着 <c>[assembly: CollectionBehavior(DisableTestParallelization = true)]</c>，
/// 32 核机器上所有用例串行执行。那条开关是为了压住 MCP/Web 的真实端口绑定，
/// 但它的代价是把互不相干的用例也一并串起来。
///
/// 现在按资源归组：<b>组内串行</b>（保住原有的确定性），<b>组间并行</b>。
/// 网关组内串行；未标注集合的纯逻辑用例各自独立并行。
/// </summary>
public static class TestCollections
{
    /// <summary>争用真实端口绑定与进程级网关状态的用例。</summary>
    public const string Gateway = "network-gateway";
}

/// <summary>网关用例串行执行：真实端口与进程级 mutex 无法并发。</summary>
[CollectionDefinition(TestCollections.Gateway, DisableParallelization = true)]
public sealed class NetworkGatewayCollection;
