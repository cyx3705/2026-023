namespace HistoryVulcan.Core.Mcp;

/// <summary>
/// 提示词治理的只读视图。宿主消费，模块提供。
/// </summary>
/// <remarks>
/// 承接 <c>IEffectivePromptDescriptionReader</c> 早就写明的分工——
/// 「治理的写方刻意在宿主之外，宿主只消费这个只读视图」。那条注释写于治理还在宿主里的时候，
/// 4.4.0 把 <c>PromptGovernanceStore</c> 迁往 HistoryPortunus 之后，它才真正成立。
///
/// **返回值刻意全部退化为基本类型。** 调用方（<c>vulcan.command.list/show</c>）只需要
/// 「改过没有、第几版、几条待审、几起事故」这四格，不需要提案与事故的完整结构。
/// 让治理的记录类型跨过这个边界，等于把宿主重新绑回模块的数据模型上——
/// 那正是这次迁移要解开的东西。
///
/// 实现方随模块热重载来去，因此消费方必须把它当作**随时可能为 null**：
/// 模块没装上、正在重载、或装载失败时，目录指令要照常可用，只是少了治理那几列。
/// </remarks>
public interface IMcpPromptGovernanceView
{
    /// <summary>当前生效的工具描述覆盖，按指令名索引；未被覆盖的指令不出现在结果中。</summary>
    IReadOnlyDictionary<string, string> EffectiveDescriptions();

    /// <summary>各指令待审提案数，按指令名索引。</summary>
    IReadOnlyDictionary<string, int> OpenProposalCounts();

    /// <summary>各指令已记录事故数，按指令名索引。</summary>
    IReadOnlyDictionary<string, int> IncidentCounts();

    /// <summary>指定指令当前生效的描述修订号；未被覆盖时为 null。</summary>
    string? CurrentRevisionId(string commandName);
}
